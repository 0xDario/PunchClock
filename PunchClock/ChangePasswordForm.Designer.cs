namespace PunchClock
{
    partial class ChangePasswordForm
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
            this.lblStaff = new System.Windows.Forms.Label();
            this.cboEmployees = new System.Windows.Forms.ComboBox();
            this.txtNewPinCode = new System.Windows.Forms.TextBox();
            this.lblOldPinCode = new System.Windows.Forms.Label();
            this.txtVerifyNewPinCode = new System.Windows.Forms.TextBox();
            this.lblNewPinCode = new System.Windows.Forms.Label();
            this.txtOldPinCode = new System.Windows.Forms.TextBox();
            this.lblVerifyNewPinCode = new System.Windows.Forms.Label();
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnUpdateStaffPassword = new System.Windows.Forms.Button();
            this.lblChangePinCode = new System.Windows.Forms.Label();
            this.PunchClockDataSet = new PunchClock.PunchClockDataSet();
            this.employeeBindingSource = new System.Windows.Forms.BindingSource(this.components);
            this.employeeTableAdapter = new PunchClock.PunchClockDataSetTableAdapters.EmployeeTableAdapter();
            ((System.ComponentModel.ISupportInitialize)(this.PunchClockDataSet)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.employeeBindingSource)).BeginInit();
            this.SuspendLayout();
            // 
            // lblStaff
            // 
            this.lblStaff.AutoSize = true;
            this.lblStaff.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblStaff.Location = new System.Drawing.Point(175, 96);
            this.lblStaff.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblStaff.Name = "lblStaff";
            this.lblStaff.Size = new System.Drawing.Size(41, 17);
            this.lblStaff.TabIndex = 2;
            this.lblStaff.Text = "Staff:";
            // 
            // cboEmployees
            // 
            this.cboEmployees.DataSource = this.employeeBindingSource;
            this.cboEmployees.DisplayMember = "FirstName";
            this.cboEmployees.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.cboEmployees.FormattingEnabled = true;
            this.cboEmployees.Location = new System.Drawing.Point(220, 90);
            this.cboEmployees.Margin = new System.Windows.Forms.Padding(2);
            this.cboEmployees.Name = "cboEmployees";
            this.cboEmployees.Size = new System.Drawing.Size(187, 28);
            this.cboEmployees.TabIndex = 3;
            this.cboEmployees.ValueMember = "EmployeeID";
            // 
            // txtNewPinCode
            // 
            this.txtNewPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtNewPinCode.Location = new System.Drawing.Point(220, 178);
            this.txtNewPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtNewPinCode.MaxLength = 6;
            this.txtNewPinCode.Name = "txtNewPinCode";
            this.txtNewPinCode.PasswordChar = '*';
            this.txtNewPinCode.Size = new System.Drawing.Size(187, 26);
            this.txtNewPinCode.TabIndex = 5;
            this.txtNewPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtNewPinCode_KeyPress);
            // 
            // lblOldPinCode
            // 
            this.lblOldPinCode.AutoSize = true;
            this.lblOldPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblOldPinCode.Location = new System.Drawing.Point(121, 139);
            this.lblOldPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblOldPinCode.Name = "lblOldPinCode";
            this.lblOldPinCode.Size = new System.Drawing.Size(95, 17);
            this.lblOldPinCode.TabIndex = 4;
            this.lblOldPinCode.Text = "Old Pin Code:";
            // 
            // txtVerifyNewPinCode
            // 
            this.txtVerifyNewPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtVerifyNewPinCode.Location = new System.Drawing.Point(220, 221);
            this.txtVerifyNewPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtVerifyNewPinCode.MaxLength = 6;
            this.txtVerifyNewPinCode.Name = "txtVerifyNewPinCode";
            this.txtVerifyNewPinCode.PasswordChar = '*';
            this.txtVerifyNewPinCode.Size = new System.Drawing.Size(187, 26);
            this.txtVerifyNewPinCode.TabIndex = 7;
            this.txtVerifyNewPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtVerifyNewPinCode_KeyPress);
            // 
            // lblNewPinCode
            // 
            this.lblNewPinCode.AutoSize = true;
            this.lblNewPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblNewPinCode.Location = new System.Drawing.Point(116, 182);
            this.lblNewPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblNewPinCode.Name = "lblNewPinCode";
            this.lblNewPinCode.Size = new System.Drawing.Size(100, 17);
            this.lblNewPinCode.TabIndex = 6;
            this.lblNewPinCode.Text = "New Pin Code:";
            // 
            // txtOldPinCode
            // 
            this.txtOldPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtOldPinCode.Location = new System.Drawing.Point(220, 135);
            this.txtOldPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtOldPinCode.MaxLength = 6;
            this.txtOldPinCode.Name = "txtOldPinCode";
            this.txtOldPinCode.PasswordChar = '*';
            this.txtOldPinCode.Size = new System.Drawing.Size(187, 26);
            this.txtOldPinCode.TabIndex = 9;
            this.txtOldPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtOldPinCode_KeyPress);
            // 
            // lblVerifyNewPinCode
            // 
            this.lblVerifyNewPinCode.AutoSize = true;
            this.lblVerifyNewPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblVerifyNewPinCode.Location = new System.Drawing.Point(76, 225);
            this.lblVerifyNewPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblVerifyNewPinCode.Name = "lblVerifyNewPinCode";
            this.lblVerifyNewPinCode.Size = new System.Drawing.Size(140, 17);
            this.lblVerifyNewPinCode.TabIndex = 8;
            this.lblVerifyNewPinCode.Text = "Verfiy New Pin Code:";
            // 
            // btnCancel
            // 
            this.btnCancel.BackColor = System.Drawing.Color.Red;
            this.btnCancel.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F);
            this.btnCancel.Location = new System.Drawing.Point(273, 294);
            this.btnCancel.Margin = new System.Windows.Forms.Padding(2);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(134, 38);
            this.btnCancel.TabIndex = 12;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = false;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // btnUpdateStaffPassword
            // 
            this.btnUpdateStaffPassword.BackColor = System.Drawing.Color.Lime;
            this.btnUpdateStaffPassword.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F);
            this.btnUpdateStaffPassword.Location = new System.Drawing.Point(82, 294);
            this.btnUpdateStaffPassword.Margin = new System.Windows.Forms.Padding(2);
            this.btnUpdateStaffPassword.Name = "btnUpdateStaffPassword";
            this.btnUpdateStaffPassword.Size = new System.Drawing.Size(134, 38);
            this.btnUpdateStaffPassword.TabIndex = 11;
            this.btnUpdateStaffPassword.Text = "Update Password";
            this.btnUpdateStaffPassword.UseVisualStyleBackColor = false;
            this.btnUpdateStaffPassword.Click += new System.EventHandler(this.btnUpdateStaffPassword_Click);
            // 
            // lblChangePinCode
            // 
            this.lblChangePinCode.AutoSize = true;
            this.lblChangePinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblChangePinCode.Location = new System.Drawing.Point(115, 32);
            this.lblChangePinCode.Name = "lblChangePinCode";
            this.lblChangePinCode.Size = new System.Drawing.Size(286, 20);
            this.lblChangePinCode.TabIndex = 12;
            this.lblChangePinCode.Text = "Note: Pin Codes must be 3-6 digits";
            // 
            // PunchClockDataSet
            // 
            this.PunchClockDataSet.DataSetName = "PunchClockDataSet";
            this.PunchClockDataSet.SchemaSerializationMode = System.Data.SchemaSerializationMode.IncludeSchema;
            // 
            // employeeBindingSource
            // 
            this.employeeBindingSource.DataMember = "Employee";
            this.employeeBindingSource.DataSource = this.PunchClockDataSet;
            // 
            // employeeTableAdapter
            // 
            this.employeeTableAdapter.ClearBeforeFill = true;
            // 
            // ChangePasswordForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(499, 376);
            this.Controls.Add(this.lblChangePinCode);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnUpdateStaffPassword);
            this.Controls.Add(this.txtOldPinCode);
            this.Controls.Add(this.lblVerifyNewPinCode);
            this.Controls.Add(this.txtVerifyNewPinCode);
            this.Controls.Add(this.lblNewPinCode);
            this.Controls.Add(this.txtNewPinCode);
            this.Controls.Add(this.lblOldPinCode);
            this.Controls.Add(this.lblStaff);
            this.Controls.Add(this.cboEmployees);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
            this.Name = "ChangePasswordForm";
            this.Text = "Change Pin Code";
            this.Load += new System.EventHandler(this.ChangePasswordForm_Load);
            ((System.ComponentModel.ISupportInitialize)(this.PunchClockDataSet)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.employeeBindingSource)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblStaff;
        private System.Windows.Forms.ComboBox cboEmployees;
        private System.Windows.Forms.TextBox txtNewPinCode;
        private System.Windows.Forms.Label lblOldPinCode;
        private System.Windows.Forms.TextBox txtVerifyNewPinCode;
        private System.Windows.Forms.Label lblNewPinCode;
        private System.Windows.Forms.TextBox txtOldPinCode;
        private System.Windows.Forms.Label lblVerifyNewPinCode;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Button btnUpdateStaffPassword;
        private System.Windows.Forms.Label lblChangePinCode;
        private PunchClockDataSet PunchClockDataSet;
        private System.Windows.Forms.BindingSource employeeBindingSource;
        private PunchClockDataSetTableAdapters.EmployeeTableAdapter employeeTableAdapter;
    }
}