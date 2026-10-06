using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Reports;
using PunchClock.Core.Site;
using PunchClock.Core.Security;

namespace PunchClock.App;

/// <summary>
/// Site administration for a signed-in admin, or staff management for a signed-in manager.
/// Every change is attributed to that account by the database's audit triggers, which also
/// enforce the role rules; this window only collects input.
/// </summary>
public partial class AdminWindow : Window
{
    /// <summary>An account list row with the linked employee's name.</summary>
    public sealed record AccountRow(AppUser User, string Employee);

    /// <summary>An employee-link choice; a null id means no link.</summary>
    public sealed record EmployeeChoice(long? Id, string Label);

    private readonly AppServices _services;
    private readonly AppUser _admin;
    private bool _signedOut;

    public AdminWindow(AppServices services, AppUser admin)
    {
        InitializeComponent();
        _services = services;
        _admin = admin;
        SignedInAs.Text = $"Signed in as {admin.DisplayName} ({admin.Username})";
        TimeZoneBox.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        if (admin.Role != UserRole.Admin)
        {
            // Managers look after staff; site settings and accounts are admin-only in the database.
            SiteTab.Visibility = AccountsTab.Visibility = Visibility.Collapsed;
            EmployeesTab.IsSelected = true;
        }

        var today = DateTime.Today;
        CorrectionFrom.SelectedDate = today.AddDays(-13);
        CorrectionTo.SelectedDate = today;
        CorrectionDate.SelectedDate = today;
        ReportFrom.SelectedDate = today.AddDays(-13);
        ReportTo.SelectedDate = today;

        Loaded += async (_, _) => await RunAsync(async () =>
        {
            await ReloadAsync();
            if (admin.Role == UserRole.Admin)
            {
                await ReloadWarningsAsync();
            }
        });
        Closed += async (_, _) => await SignOutOnceAsync();
    }

    private AuditActor Actor => AuditActor.ForUser(_admin.Id);

    private async Task ReloadAsync()
    {
        var zoneId = await _services.Site.GetTimeZoneIdAsync();
        TimeZoneBox.SelectedValue = zoneId ?? TimeZoneInfo.Local.Id;
        CurrentZone.Text = zoneId is null
            ? "Not set. Nobody can punch until a site time zone is saved."
            : $"Saved: {zoneId}";

        var employees = await _services.Employees.ListAsync(activeOnly: false);
        EmployeeList.ItemsSource = employees;

        var names = employees.ToDictionary(e => e.Id, e => e.DisplayName);
        AccountList.ItemsSource = (await _services.Accounts.ListAsync())
            .Select(u => new AccountRow(u, u.EmployeeId is { } id ? names.GetValueOrDefault(id, $"#{id}") : ""))
            .ToList();
        var choices = employees.Where(e => e.IsActive)
            .Select(e => new EmployeeChoice(e.Id, e.LegacyId is { } legacy ? $"{e.DisplayName} (#{legacy})" : e.DisplayName))
            .Prepend(new EmployeeChoice(null, "(does not punch)"))
            .ToList();
        AccountLinkEmployee.ItemsSource = choices;
        NewAccountEmployee.ItemsSource = choices;
        NewAccountEmployee.SelectedIndex = 0;

        var selectedId = (CorrectionEmployee.SelectedItem as Employee)?.Id;
        CorrectionEmployee.ItemsSource = employees;
        CorrectionEmployee.SelectedItem = employees.FirstOrDefault(e => e.Id == selectedId);
        await ReloadPunchesAsync();
    }

    private async Task ReloadPunchesAsync()
    {
        if (CorrectionEmployee.SelectedItem is not Employee employee
            || CorrectionFrom.SelectedDate is not { } from || CorrectionTo.SelectedDate is not { } to)
        {
            PunchList.ItemsSource = null;
            return;
        }

        // The dates are site dates; widen by a day each side so any zone is covered, then trim.
        var zone = SiteTime.ResolveZone(await _services.Site.GetTimeZoneIdAsync(), TimeZoneInfo.Local);
        var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(from, to);
        var rows = await _services.Corrections.ListAsync(employee.Id, fromUtc, toUtc);
        PunchList.ItemsSource = rows
            .Where(r =>
            {
                var date = TimeZoneInfo.ConvertTime(r.Punch.OccurredAtUtc, zone).Date;
                return date >= from.Date && date <= to.Date;
            })
            .ToList();
    }

    private async void CorrectionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            await RunAsync(ReloadPunchesAsync);
        }
    }

    private async void ShowPunches_Click(object sender, RoutedEventArgs e) => await RunAsync(ReloadPunchesAsync);

    private void PunchList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Pre-fill the editor with the selected punch, ready to change.
        if (PunchList.SelectedItem is PunchReviewRow { Punch: var punch })
        {
            CorrectionDirection.SelectedIndex = punch.Direction == PunchDirection.In ? 0 : 1;
            CorrectionDate.SelectedDate = punch.OccurredAtLocal.Date;
            CorrectionTime.Text = punch.OccurredAtLocal.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async void AddPunch_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectionEmployee.SelectedItem is not Employee employee)
        {
            Show("Select an employee first.", isError: true);
            return;
        }

        if (ReadCorrectionTime() is not { } local)
        {
            return;
        }

        var (direction, reason) = (SelectedDirection, CorrectionReason.Text);
        await CorrectAsync(() => _services.Corrections.AddAsync(_admin, employee.Id, direction, local, reason));
    }

    private async void AdjustPunch_Click(object sender, RoutedEventArgs e)
    {
        if (PunchList.SelectedItem is not PunchReviewRow row)
        {
            Show("Select a punch first.", isError: true);
            return;
        }

        if (ReadCorrectionTime() is not { } local)
        {
            return;
        }

        var (direction, reason) = (SelectedDirection, CorrectionReason.Text);
        await CorrectAsync(() => _services.Corrections.AdjustAsync(_admin, row.Punch.Id, direction, local, reason));
    }

    private async void VoidPunch_Click(object sender, RoutedEventArgs e)
    {
        if (PunchList.SelectedItem is not PunchReviewRow row)
        {
            Show("Select a punch first.", isError: true);
            return;
        }

        var reason = CorrectionReason.Text;
        await CorrectAsync(() => _services.Corrections.VoidAsync(_admin, row.Punch.Id, reason));
    }

    private PunchDirection SelectedDirection => CorrectionDirection.SelectedIndex == 1 ? PunchDirection.Out : PunchDirection.In;

    private DateTime? ReadCorrectionTime()
    {
        if (CorrectionDate.SelectedDate is not { } date
            || !TimeOnly.TryParse(CorrectionTime.Text, System.Globalization.CultureInfo.CurrentCulture, out var time))
        {
            Show("Enter a date and a time such as 17:30.", isError: true);
            return null;
        }

        return date.Date + time.ToTimeSpan();
    }

    private async Task CorrectAsync(Func<Task<CorrectionResult>> correct)
    {
        await RunAsync(async () =>
        {
            var result = await correct();
            if (result == CorrectionResult.Corrected)
            {
                CorrectionReason.Clear();
            }

            await ReloadPunchesAsync();
            Show(result switch
            {
                CorrectionResult.Corrected => "Correction saved.",
                CorrectionResult.ReasonTooShort => $"Enter a reason of at least {PunchCorrectionService.MinReasonLength} characters.",
                CorrectionResult.OwnPunches => "Your account is linked to this employee; another manager must correct these punches.",
                CorrectionResult.InFuture => "That time is in the future.",
                CorrectionResult.InvalidLocalTime => "That time does not exist on that date (daylight-saving change). Pick another time.",
                CorrectionResult.SiteTimeZoneNotSet => "An admin must set the site time zone first.",
                CorrectionResult.PunchNotFound => "That punch has already been corrected. Refresh and select the current one.",
                CorrectionResult.NotAllowed => "Only managers and admins can correct punches.",
                _ => "That employee no longer exists.",
            }, isError: result != CorrectionResult.Corrected);
        });
    }

    private async void SaveTimeZone_Click(object sender, RoutedEventArgs e)
    {
        if (TimeZoneBox.SelectedValue is not string zoneId)
        {
            Show("Pick a time zone first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            await _services.Site.SetTimeZoneAsync(_admin, zoneId);
            await ReloadAsync();
            Show($"Site time zone set to {zoneId}.");
        });
    }

    private async Task ReloadWarningsAsync()
    {
        var warnings = await Task.Run(() => _services.Maintenance.FindWarningsAsync());
        WarningList.ItemsSource = warnings;
        WarningList.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningSummary.Text = warnings.Count == 0
            ? "None. The PC clock never ran backwards and every punch's time zone offset matches."
            : $"{warnings.Count} to review. The PC clock ran backwards, or a punch's saved offset differs from today's time zone "
              + "rules (for example after a Windows time zone update). Neither proves tampering: check each against paper records.";
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up the PunchClock database",
            Filter = "PunchClock backup (*.db)|*.db",
            DefaultExt = ".db",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = $"punchclock-backup-{DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.db",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _services.Maintenance.BackupAsync(_admin, path));
            BackupResult.Text = $"Saved and verified: {result.Path} ({result.Bytes / 1024.0:N0} KB). "
                + $"It holds the audit log up to entry {result.AuditHeadSeq}. SHA-256 {result.Sha256}.";
            Show("Backup saved, verified and recorded in the audit log.");
        });
    }

    private (DateOnly From, DateOnly To)? ReadReportPeriod()
    {
        if (ReportFrom.SelectedDate is not { } from || ReportTo.SelectedDate is not { } to || to < from)
        {
            Show("Pick the first and last day of the period.", isError: true);
            return null;
        }

        return (DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
    }

    private async void RunReport_Click(object sender, RoutedEventArgs e)
    {
        if (ReadReportPeriod() is not { } period)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var report = await Task.Run(() => _services.Reports.PayReportAsync(_admin, period.From, period.To));
            PayLines.ItemsSource = report.Lines;
            UncountedShifts.ItemsSource = report.NotCounted;
            ReportTotal.Text = $"{report.Lines.Sum(l => l.Shifts)} shifts, {report.TotalMinutes / 60m:0.00} hours";
            Show(report.NotCounted.Count == 0
                ? "Report ready."
                : $"Report ready. {report.NotCounted.Count} shift(s) are not counted in this period; see the list below.");
        });
    }

    private async void ExportPayReport_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync("pay-report", (from, to, path) => _services.Reports.ExportPayReportAsync(_admin, from, to, path));

    private async void ExportPunches_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync("punches", (from, to, path) => _services.Reports.ExportPunchesAsync(_admin, from, to, path));

    private async void ExportCorrections_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync("corrections", (from, to, path) => _services.Reports.ExportCorrectionsAsync(_admin, from, to, path));

    private async Task ExportAsync(string kind, Func<DateOnly, DateOnly, string, Task<ExportResult>> export)
    {
        if (ReadReportPeriod() is not { } period)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export to CSV",
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = string.Create(CultureInfo.InvariantCulture, $"{kind}-{period.From:yyyy-MM-dd}-to-{period.To:yyyy-MM-dd}.csv"),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => export(period.From, period.To, path));
            Show($"Saved {result.Rows} row(s) to {result.Path} and recorded the export in the audit log.");
        });
    }

    private async void DeactivateAccount_Click(object sender, RoutedEventArgs e) => await SetAccountActiveAsync(false);

    private async void ActivateAccount_Click(object sender, RoutedEventArgs e) => await SetAccountActiveAsync(true);

    private async Task SetAccountActiveAsync(bool isActive)
    {
        if ((AccountList.SelectedItem as AccountRow)?.User is not { } target)
        {
            Show("Select an account first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _services.Accounts.SetActiveAsync(_admin, target.Id, isActive, AccountReason.Text);
            await ReloadAsync();
            Show(result switch
            {
                AccountChangeResult.Changed => $"{target.Username} is now {(isActive ? "active" : "inactive")}.",
                AccountChangeResult.NotAllowed => "The system account and your own account cannot be changed here.",
                _ => "That account no longer exists.",
            }, isError: result != AccountChangeResult.Changed);
        });
    }

    private void AccountList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if ((AccountList.SelectedItem as AccountRow)?.User is { } user)
        {
            AccountLinkEmployee.SelectedItem = AccountLinkEmployee.Items.Cast<EmployeeChoice>().FirstOrDefault(c => c.Id == user.EmployeeId);
        }
    }

    private async void SetAccountLink_Click(object sender, RoutedEventArgs e)
    {
        if ((AccountList.SelectedItem as AccountRow)?.User is not { } target || AccountLinkEmployee.SelectedItem is not EmployeeChoice link)
        {
            Show("Select an account and the employee they punch as.", isError: true);
            return;
        }

        var reason = AccountReason.Text;
        await RunAsync(async () =>
        {
            var result = await _services.Accounts.SetEmployeeLinkAsync(_admin, target.Id, link.Id, reason);
            await ReloadAsync();
            Show(result switch
            {
                AccountChangeResult.Changed => link.Id is null
                    ? $"{target.Username} is no longer linked to an employee."
                    : $"{target.Username} now punches as {link.Label} and cannot correct those punches.",
                _ => AccountMessage(result),
            }, isError: result != AccountChangeResult.Changed);
        });
    }

    private async void ResetAccountPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((AccountList.SelectedItem as AccountRow)?.User is not { } target)
        {
            Show("Select an account first.", isError: true);
            return;
        }

        var (password, reason) = (AccountResetPassword.Password, AccountReason.Text);
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _services.Accounts.ResetPasswordAsync(_admin, target.Id, password, reason));
            if (result == AccountChangeResult.Changed)
            {
                AccountResetPassword.Clear();
            }

            await ReloadAsync();
            Show(result == AccountChangeResult.Changed
                ? $"{target.Username}'s password was reset. They must choose their own at their next sign-in."
                : AccountMessage(result), isError: result != AccountChangeResult.Changed);
        });
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var (username, name, password) = (NewAccountUsername.Text, NewAccountName.Text, NewAccountPassword.Password);
        var role = NewAccountRole.SelectedIndex == 1 ? UserRole.Admin : UserRole.Manager;
        var employeeId = (NewAccountEmployee.SelectedItem as EmployeeChoice)?.Id;
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _services.Accounts.CreateAccountAsync(_admin, username, name, role, password, employeeId));
            if (result == AccountChangeResult.Changed)
            {
                NewAccountUsername.Clear();
                NewAccountName.Clear();
                NewAccountPassword.Clear();
            }

            await ReloadAsync();
            Show(result == AccountChangeResult.Changed
                ? $"{username.Trim()} added. They must choose their own password at first sign-in."
                : AccountMessage(result), isError: result != AccountChangeResult.Changed);
        });
    }

    private async void ChangeMyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (new ChangePasswordWindow(_services.Accounts, _admin, forced: false) { Owner = this }.ShowDialog() == true)
        {
            Show("Your password was changed.");
        }

        await RunAsync(ReloadAsync);
    }

    private static string AccountMessage(AccountChangeResult result) => result switch
    {
        AccountChangeResult.NotAllowed => "Not allowed: fill in every field, and your own account and service accounts cannot be changed here.",
        AccountChangeResult.ReasonRequired => "Enter a reason first.",
        AccountChangeResult.PasswordRejected => $"Passwords must be at least {AccountService.MinPasswordLength} characters.",
        AccountChangeResult.UsernameTaken => "That username is already in use.",
        AccountChangeResult.EmployeeNotFound => "That employee no longer exists.",
        AccountChangeResult.EmployeeAlreadyLinked => "That employee is already linked to another account.",
        _ => "That account no longer exists.",
    };

    private async void DeactivateEmployee_Click(object sender, RoutedEventArgs e) => await SetEmployeeActiveAsync(false);

    private async void ActivateEmployee_Click(object sender, RoutedEventArgs e) => await SetEmployeeActiveAsync(true);

    private async Task SetEmployeeActiveAsync(bool isActive)
    {
        if (EmployeeList.SelectedItem is not Employee target)
        {
            Show("Select an employee first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _services.Employees.SetActiveAsync(Actor, target.Id, isActive);
            await ReloadAsync();
            Show(result switch
            {
                EmployeeChangeResult.Changed => $"{target.DisplayName} is now {(isActive ? "active" : "inactive")}.",
                EmployeeChangeResult.PunchedIn => $"{target.DisplayName} is punched in. Add their punch-out on the Corrections tab first.",
                EmployeeChangeResult.DuplicateName => $"Another active employee is named {target.DisplayName}. Rename or deactivate one first.",
                _ => "That employee no longer exists.",
            }, isError: result != EmployeeChangeResult.Changed);
        });
    }

    private async void ResetPin_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeList.SelectedItem is not Employee target)
        {
            Show("Select an employee first.", isError: true);
            return;
        }

        var (pin, reason) = (TemporaryPin.Password, PinResetReason.Text);
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _services.Employees.ResetPinAsync(_admin, target.Id, pin, reason));
            if (result == PinResetResult.Reset)
            {
                TemporaryPin.Clear();
            }

            await ReloadAsync();
            Show(result switch
            {
                PinResetResult.Reset => $"{target.DisplayName}'s PIN was reset. They must choose a new one at their next punch.",
                PinResetResult.PinRejected => PinPolicy.Validate(pin) ?? "That PIN is not allowed.",
                PinResetResult.NotAllowed => "Enter a reason for the reset.",
                _ => "That employee no longer exists or is inactive.",
            }, isError: result != PinResetResult.Reset);
        });
    }

    private async void AddEmployee_Click(object sender, RoutedEventArgs e)
    {
        var (first, last, pin) = (NewFirstName.Text, NewLastName.Text, NewPin.Password);
        await RunAsync(async () =>
        {
            await Task.Run(() => _services.Employees.CreateAsync(Actor, first, last, pin));
            NewFirstName.Clear();
            NewLastName.Clear();
            NewPin.Clear();
            await ReloadAsync();
            Show($"{first.Trim()} {last.Trim()} added.");
        });
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await SignOutOnceAsync();
        Close();
    }

    private async Task SignOutOnceAsync()
    {
        if (_signedOut)
        {
            return;
        }

        _signedOut = true;
        try
        {
            await _services.Accounts.SignOutAsync(_admin);
        }
        catch
        {
            // Sign-out is a courtesy record; the window closes regardless.
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // Includes the database's own refusals, e.g. "site_setting: admin only".
            Show(ex.Message, isError: true);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Show(string message, bool isError = false)
    {
        Status.Text = message;
        Status.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkGreen;
    }

    private void Pin_PreviewTextInput(object sender, TextCompositionEventArgs e) => DigitInput.Filter(e);

    private void Pin_Pasting(object sender, DataObjectPastingEventArgs e) => DigitInput.FilterPaste(e);
}
