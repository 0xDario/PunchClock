
namespace PunchClock
{
    partial class PunchClockForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.btnPunchInOut = new System.Windows.Forms.Button();
            this.cboEmployees = new System.Windows.Forms.ComboBox();
            this.employeeBindingSource = new System.Windows.Forms.BindingSource(this.components);
            this.savoiaPunchClockDataSet = new PunchClock.SavoiaPunchClockDataSet();
            this.lblStaff = new System.Windows.Forms.Label();
            this.lblPinCode = new System.Windows.Forms.Label();
            this.txtPinCode = new System.Windows.Forms.TextBox();
            this.employeeTableAdapter = new PunchClock.SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter();
            this.shiftBindingSource = new System.Windows.Forms.BindingSource(this.components);
            this.shiftTableAdapter = new PunchClock.SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter();
            this.btnChangePassword = new System.Windows.Forms.Button();
            this.groupBoxStaffActions = new System.Windows.Forms.GroupBox();
            this.btnNewStaff = new System.Windows.Forms.Button();
            this.lblInstructions = new System.Windows.Forms.Label();
            this.lblClock = new System.Windows.Forms.Label();
            this.lblDate = new System.Windows.Forms.Label();
            this.timer1 = new System.Windows.Forms.Timer(this.components);
            ((System.ComponentModel.ISupportInitialize)(this.employeeBindingSource)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.savoiaPunchClockDataSet)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.shiftBindingSource)).BeginInit();
            this.groupBoxStaffActions.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnPunchInOut
            // 
            this.btnPunchInOut.BackColor = System.Drawing.Color.Gray;
            this.btnPunchInOut.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F);
            this.btnPunchInOut.Location = new System.Drawing.Point(391, 266);
            this.btnPunchInOut.Margin = new System.Windows.Forms.Padding(2);
            this.btnPunchInOut.Name = "btnPunchInOut";
            this.btnPunchInOut.Size = new System.Drawing.Size(264, 46);
            this.btnPunchInOut.TabIndex = 4;
            this.btnPunchInOut.UseVisualStyleBackColor = false;
            this.btnPunchInOut.Click += new System.EventHandler(this.btnPunchInOut_Click);
            // 
            // cboEmployees
            // 
            this.cboEmployees.DataSource = this.employeeBindingSource;
            this.cboEmployees.DisplayMember = "FullName";
            this.cboEmployees.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.cboEmployees.FormattingEnabled = true;
            this.cboEmployees.Location = new System.Drawing.Point(461, 168);
            this.cboEmployees.Margin = new System.Windows.Forms.Padding(2);
            this.cboEmployees.Name = "cboEmployees";
            this.cboEmployees.Size = new System.Drawing.Size(194, 28);
            this.cboEmployees.TabIndex = 1;
            this.cboEmployees.ValueMember = "EmployeeID";
            this.cboEmployees.SelectedIndexChanged += new System.EventHandler(this.cboEmployees_SelectedIndexChanged);
            // 
            // employeeBindingSource
            // 
            this.employeeBindingSource.DataMember = "Employee";
            this.employeeBindingSource.DataSource = this.savoiaPunchClockDataSet;
            // 
            // savoiaPunchClockDataSet
            // 
            this.savoiaPunchClockDataSet.DataSetName = "SavoiaPunchClockDataSet";
            this.savoiaPunchClockDataSet.SchemaSerializationMode = System.Data.SchemaSerializationMode.IncludeSchema;
            // 
            // lblStaff
            // 
            this.lblStaff.AutoSize = true;
            this.lblStaff.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblStaff.Location = new System.Drawing.Point(416, 174);
            this.lblStaff.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblStaff.Name = "lblStaff";
            this.lblStaff.Size = new System.Drawing.Size(41, 17);
            this.lblStaff.TabIndex = 0;
            this.lblStaff.Text = "Staff:";
            // 
            // lblPinCode
            // 
            this.lblPinCode.AutoSize = true;
            this.lblPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblPinCode.Location = new System.Drawing.Point(388, 219);
            this.lblPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblPinCode.Name = "lblPinCode";
            this.lblPinCode.Size = new System.Drawing.Size(69, 17);
            this.lblPinCode.TabIndex = 2;
            this.lblPinCode.Text = "Pin Code:";
            // 
            // txtPinCode
            // 
            this.txtPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtPinCode.Location = new System.Drawing.Point(461, 213);
            this.txtPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtPinCode.MaxLength = 6;
            this.txtPinCode.Name = "txtPinCode";
            this.txtPinCode.PasswordChar = '*';
            this.txtPinCode.Size = new System.Drawing.Size(194, 26);
            this.txtPinCode.TabIndex = 3;
            this.txtPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtPinCode_KeyPress);
            // 
            // employeeTableAdapter
            // 
            this.employeeTableAdapter.ClearBeforeFill = true;
            // 
            // shiftBindingSource
            // 
            this.shiftBindingSource.DataMember = "Shift";
            this.shiftBindingSource.DataSource = this.savoiaPunchClockDataSet;
            // 
            // shiftTableAdapter
            // 
            this.shiftTableAdapter.ClearBeforeFill = true;
            // 
            // btnChangePassword
            // 
            this.btnChangePassword.BackColor = System.Drawing.Color.Orange;
            this.btnChangePassword.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnChangePassword.Location = new System.Drawing.Point(5, 18);
            this.btnChangePassword.Margin = new System.Windows.Forms.Padding(2);
            this.btnChangePassword.Name = "btnChangePassword";
            this.btnChangePassword.Size = new System.Drawing.Size(134, 36);
            this.btnChangePassword.TabIndex = 7;
            this.btnChangePassword.Text = "Change Pin Code";
            this.btnChangePassword.UseVisualStyleBackColor = false;
            this.btnChangePassword.Click += new System.EventHandler(this.btnChangePassword_Click);
            // 
            // groupBoxStaffActions
            // 
            this.groupBoxStaffActions.Controls.Add(this.btnNewStaff);
            this.groupBoxStaffActions.Controls.Add(this.btnChangePassword);
            this.groupBoxStaffActions.Location = new System.Drawing.Point(12, 12);
            this.groupBoxStaffActions.Name = "groupBoxStaffActions";
            this.groupBoxStaffActions.Size = new System.Drawing.Size(146, 104);
            this.groupBoxStaffActions.TabIndex = 8;
            this.groupBoxStaffActions.TabStop = false;
            this.groupBoxStaffActions.Text = "Staff Actions";
            // 
            // btnNewStaff
            // 
            this.btnNewStaff.BackColor = System.Drawing.Color.LimeGreen;
            this.btnNewStaff.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnNewStaff.Location = new System.Drawing.Point(5, 58);
            this.btnNewStaff.Margin = new System.Windows.Forms.Padding(2);
            this.btnNewStaff.Name = "btnNewStaff";
            this.btnNewStaff.Size = new System.Drawing.Size(134, 36);
            this.btnNewStaff.TabIndex = 8;
            this.btnNewStaff.Text = "Create New Staff";
            this.btnNewStaff.UseVisualStyleBackColor = false;
            this.btnNewStaff.Click += new System.EventHandler(this.btnNewStaff_Click);
            // 
            // lblInstructions
            // 
            this.lblInstructions.AutoSize = true;
            this.lblInstructions.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblInstructions.Location = new System.Drawing.Point(292, 46);
            this.lblInstructions.Name = "lblInstructions";
            this.lblInstructions.Size = new System.Drawing.Size(437, 20);
            this.lblInstructions.TabIndex = 9;
            this.lblInstructions.Text = "Select Your Name and Enter Your Pin Code To Punch In/Out";
            // 
            // lblClock
            // 
            this.lblClock.AutoSize = true;
            this.lblClock.Font = new System.Drawing.Font("Microsoft Sans Serif", 36F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblClock.ForeColor = System.Drawing.Color.Blue;
            this.lblClock.Location = new System.Drawing.Point(36, 213);
            this.lblClock.Name = "lblClock";
            this.lblClock.Size = new System.Drawing.Size(212, 55);
            this.lblClock.TabIndex = 10;
            this.lblClock.Text = "00:00:00";
            // 
            // lblDate
            // 
            this.lblDate.AutoSize = true;
            this.lblDate.Font = new System.Drawing.Font("Microsoft Sans Serif", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblDate.ForeColor = System.Drawing.Color.Black;
            this.lblDate.Location = new System.Drawing.Point(12, 174);
            this.lblDate.Name = "lblDate";
            this.lblDate.Size = new System.Drawing.Size(58, 26);
            this.lblDate.TabIndex = 11;
            this.lblDate.Text = "Date";
            // 
            // timer1
            // 
            this.timer1.Interval = 1000;
            this.timer1.Tick += new System.EventHandler(this.timer1_Tick);
            // 
            // PunchClockForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(752, 385);
            this.Controls.Add(this.lblDate);
            this.Controls.Add(this.lblClock);
            this.Controls.Add(this.lblInstructions);
            this.Controls.Add(this.groupBoxStaffActions);
            this.Controls.Add(this.txtPinCode);
            this.Controls.Add(this.lblPinCode);
            this.Controls.Add(this.lblStaff);
            this.Controls.Add(this.cboEmployees);
            this.Controls.Add(this.btnPunchInOut);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
            this.Margin = new System.Windows.Forms.Padding(2);
            this.Name = "PunchClockForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Savoia Punch Clock";
            this.Load += new System.EventHandler(this.Form1_Load);
            ((System.ComponentModel.ISupportInitialize)(this.employeeBindingSource)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.savoiaPunchClockDataSet)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.shiftBindingSource)).EndInit();
            this.groupBoxStaffActions.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnPunchInOut;
        private System.Windows.Forms.ComboBox cboEmployees;
        private System.Windows.Forms.Label lblStaff;
        private System.Windows.Forms.Label lblPinCode;
        private System.Windows.Forms.TextBox txtPinCode;
        private SavoiaPunchClockDataSet savoiaPunchClockDataSet;
        private System.Windows.Forms.BindingSource employeeBindingSource;
        private SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter employeeTableAdapter;
        private System.Windows.Forms.BindingSource shiftBindingSource;
        private SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter shiftTableAdapter;
        private System.Windows.Forms.Button btnChangePassword;
        private System.Windows.Forms.GroupBox groupBoxStaffActions;
        private System.Windows.Forms.Label lblInstructions;
        private System.Windows.Forms.Button btnNewStaff;
        private System.Windows.Forms.Label lblClock;
        private System.Windows.Forms.Label lblDate;
        private System.Windows.Forms.Timer timer1;
    }
}

