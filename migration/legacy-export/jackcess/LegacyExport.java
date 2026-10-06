import com.healthmarketscience.jackcess.Column;
import com.healthmarketscience.jackcess.Cursor;
import com.healthmarketscience.jackcess.CursorBuilder;
import com.healthmarketscience.jackcess.DataType;
import com.healthmarketscience.jackcess.Database;
import com.healthmarketscience.jackcess.DatabaseBuilder;
import com.healthmarketscience.jackcess.DateTimeType;
import com.healthmarketscience.jackcess.Row;
import com.healthmarketscience.jackcess.Table;
import com.healthmarketscience.jackcess.crypt.CryptCodecProvider;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDateTime;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;

/**
 * Cross-platform exporter for the legacy PunchClock.accdb (Employee, Shift).
 * Produces the same CSV layout and manifest as Export-LegacyData.ps1 so either
 * tool's output can be reconciled against the other. See ../README.md.
 *
 * Usage: java -cp 'lib/*' LegacyExport.java <path-to-accdb> [outDir] [password]
 */
public class LegacyExport {
    static final String[] TABLES = {"Employee", "Shift"};
    static final String TOOL = "jackcess-LegacyExport/1";
    static final LocalDateTime OA_EPOCH = LocalDateTime.of(1899, 12, 30, 0, 0);
    static final DateTimeFormatter ISO_MS = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS");
    static final long MS_PER_DAY = 86_400_000L;

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("usage: LegacyExport <accdb> [outDir] [password]");
            System.exit(2);
        }
        Path source = Path.of(args[0]).toAbsolutePath().normalize();
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss"));
        Path out = Path.of(args.length > 1 ? args[1] : "legacy-export-" + stamp).toAbsolutePath();
        String password = args.length > 2 ? args[2] : "admin123";

        if (Files.exists(out) && Files.list(out).findAny().isPresent())
            throw new IllegalStateException("output folder is not empty: " + out);
        Files.createDirectories(out.resolve("source"));

        // Snapshot the source first and export from the snapshot, so the exported
        // rows are provably from the exact bytes recorded in the manifest.
        String sourceHash = sha256(source);
        Path snapshot = out.resolve("source").resolve(source.getFileName());
        Files.copy(source, snapshot, StandardCopyOption.COPY_ATTRIBUTES);
        if (!sha256(snapshot).equals(sourceHash))
            throw new IllegalStateException("snapshot hash differs from source; source changed during copy");

        StringBuilder tablesJson = new StringBuilder();
        try (Database db = new DatabaseBuilder(snapshot.toFile())
                .setReadOnly(true)
                .setCodecProvider(new CryptCodecProvider(password))
                .open()) {
            db.setDateTimeType(DateTimeType.LOCAL_DATE_TIME);
            for (String name : TABLES) {
                if (tablesJson.length() > 0) tablesJson.append(",\n");
                tablesJson.append(exportTable(db.getTable(name), out));
            }
        }

        // The export opened the snapshot read-only; prove it is still byte-identical.
        if (!sha256(snapshot).equals(sourceHash))
            throw new IllegalStateException("snapshot changed during export");

        String manifest = "{\n"
                + "  \"tool\": " + q(TOOL) + ",\n"
                + "  \"exported_at_utc\": " + q(Instant.now().toString()) + ",\n"
                + "  \"source\": {\n"
                + "    \"path\": " + q(source.toString()) + ",\n"
                + "    \"size_bytes\": " + Files.size(source) + ",\n"
                + "    \"last_write_utc\": " + q(Files.getLastModifiedTime(source).toInstant().toString()) + ",\n"
                + "    \"sha256\": " + q(sourceHash) + ",\n"
                + "    \"snapshot\": " + q("source/" + source.getFileName()) + "\n"
                + "  },\n"
                + "  \"timestamps\": \"site-local wall clock, no offset; *_OADate is the Access date double\",\n"
                + "  \"tables\": [\n" + tablesJson + "\n  ]\n"
                + "}\n";
        Files.writeString(out.resolve("manifest.json"), manifest, StandardCharsets.UTF_8);
        System.out.print(manifest);
    }

    static String exportTable(Table table, Path out) throws IOException {
        Column pk = table.getPrimaryKeyIndex().getColumns().get(0).getColumn();
        List<? extends Column> cols = table.getColumns();

        List<String> header = new ArrayList<>();
        for (Column c : cols) {
            header.add(c.getName());
            if (isDate(c)) header.add(c.getName() + "_OADate");
        }

        StringBuilder csv = new StringBuilder(String.join(",", header.stream().map(LegacyExport::field).toList())).append("\r\n");
        long rows = 0;
        Long minId = null, maxId = null;
        Cursor cursor = CursorBuilder.createCursor(table.getPrimaryKeyIndex());
        for (Row row : cursor) {
            List<String> fields = new ArrayList<>();
            for (Column c : cols) {
                Object v = row.get(c.getName());
                if (isDate(c)) {
                    LocalDateTime t = (LocalDateTime) v;
                    fields.add(t == null ? "" : t.format(ISO_MS));
                    fields.add(t == null ? "" : formatDouble(toOADate(t)));
                } else {
                    fields.add(field(v));
                }
            }
            csv.append(String.join(",", fields)).append("\r\n");
            long id = ((Number) row.get(pk.getName())).longValue();
            minId = minId == null ? id : Math.min(minId, id);
            maxId = maxId == null ? id : Math.max(maxId, id);
            rows++;
        }

        // Count again through an independent path (physical table scan instead of the
        // PK index). The row count stored in the table header is NOT used: Access
        // only refreshes it on compact, and the repo copies are off by up to 12.
        long scanned = 0;
        for (Row ignored : table) scanned++;
        if (rows != scanned)
            throw new IllegalStateException(table.getName() + ": wrote " + rows + " rows via PK index, table scan found " + scanned);

        Path file = out.resolve(table.getName() + ".csv");
        Files.writeString(file, csv, StandardCharsets.UTF_8);

        return "    {\n"
                + "      \"name\": " + q(table.getName()) + ",\n"
                + "      \"file\": " + q(table.getName() + ".csv") + ",\n"
                + "      \"row_count\": " + rows + ",\n"
                + "      \"count_check\": " + q("table scan = " + scanned) + ",\n"
                + "      \"primary_key\": " + q(pk.getName()) + ",\n"
                + "      \"min_id\": " + minId + ",\n"
                + "      \"max_id\": " + maxId + ",\n"
                + "      \"columns\": [" + String.join(", ", header.stream().map(LegacyExport::q).toList()) + "],\n"
                + "      \"sha256\": " + q(sha256(file)) + "\n"
                + "    }";
    }

    static boolean isDate(Column c) {
        return c.getType() == DataType.SHORT_DATE_TIME || c.getType() == DataType.EXT_DATE_TIME;
    }

    /** Same arithmetic as .NET DateTime.ToOADate, so both exporters emit identical doubles. */
    static double toOADate(LocalDateTime t) {
        long ms = Duration.between(OA_EPOCH, t).toMillis();
        // Before the epoch the integer part counts days back and the fraction counts
        // time forward, e.g. 1899-12-29 06:00 is -1.25.
        if (ms < 0) {
            long frac = ms % MS_PER_DAY;
            if (frac != 0) ms -= (MS_PER_DAY + frac) * 2;
        }
        return (double) ms / MS_PER_DAY;
    }

    /** Shortest round-trip decimal, no trailing ".0", matching the .NET side. */
    static String formatDouble(double d) {
        String s = Double.toString(d);
        return s.endsWith(".0") ? s.substring(0, s.length() - 2) : s;
    }

    /** RFC 4180 field: NULL is empty, empty string is "", quote when needed. */
    static String field(Object v) {
        if (v == null) return "";
        String s = v.toString();
        if (s.isEmpty() || s.contains(",") || s.contains("\"") || s.contains("\r") || s.contains("\n")
                || !s.equals(s.strip()))
            return "\"" + s.replace("\"", "\"\"") + "\"";
        return s;
    }

    static String q(String s) {
        return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\"";
    }

    static String sha256(Path p) throws IOException {
        try (InputStream in = Files.newInputStream(p)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[1 << 16];
            for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
            return HexFormat.of().formatHex(md.digest());
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
