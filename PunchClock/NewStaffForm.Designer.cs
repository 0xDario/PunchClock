namespace PunchClock
{
    partial class NewStaffForm
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
            this.txtFirstName = new System.Windows.Forms.TextBox();
            this.lblFirstName = new System.Windows.Forms.Label();
            this.txtPinCode = new System.Windows.Forms.TextBox();
            this.lblNewStaffPinCode = new System.Windows.Forms.Label();
            this.txtVerifyPinCode = new System.Windows.Forms.TextBox();
            this.lblVerifyNewStaffPinCode = new System.Windows.Forms.Label();
            this.txtLastName = new System.Windows.Forms.TextBox();
            this.lblLastName = new System.Windows.Forms.Label();
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnSaveNewStaff = new System.Windows.Forms.Button();
            this.lblPinCodeRequirements = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // txtFirstName
            // 
            this.txtFirstName.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtFirstName.Location = new System.Drawing.Point(216, 31);
            this.txtFirstName.Margin = new System.Windows.Forms.Padding(2);
            this.txtFirstName.MaxLength = 50;
            this.txtFirstName.Name = "txtFirstName";
            this.txtFirstName.Size = new System.Drawing.Size(187, 26);
            this.txtFirstName.TabIndex = 1;
            // 
            // lblFirstName
            // 
            this.lblFirstName.AutoSize = true;
            this.lblFirstName.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblFirstName.Location = new System.Drawing.Point(132, 37);
            this.lblFirstName.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblFirstName.Name = "lblFirstName";
            this.lblFirstName.Size = new System.Drawing.Size(80, 17);
            this.lblFirstName.TabIndex = 0;
            this.lblFirstName.Text = "First Name:";
            // 
            // txtPinCode
            // 
            this.txtPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtPinCode.Location = new System.Drawing.Point(216, 151);
            this.txtPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtPinCode.MaxLength = 6;
            this.txtPinCode.Name = "txtPinCode";
            this.txtPinCode.PasswordChar = '*';
            this.txtPinCode.Size = new System.Drawing.Size(187, 26);
            this.txtPinCode.TabIndex = 6;
            this.txtPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtPinCode_KeyPress);
            // 
            // lblNewStaffPinCode
            // 
            this.lblNewStaffPinCode.AutoSize = true;
            this.lblNewStaffPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblNewStaffPinCode.Location = new System.Drawing.Point(143, 157);
            this.lblNewStaffPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblNewStaffPinCode.Name = "lblNewStaffPinCode";
            this.lblNewStaffPinCode.Size = new System.Drawing.Size(69, 17);
            this.lblNewStaffPinCode.TabIndex = 5;
            this.lblNewStaffPinCode.Text = "Pin Code:";
            // 
            // txtVerifyPinCode
            // 
            this.txtVerifyPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtVerifyPinCode.Location = new System.Drawing.Point(216, 196);
            this.txtVerifyPinCode.Margin = new System.Windows.Forms.Padding(2);
            this.txtVerifyPinCode.MaxLength = 6;
            this.txtVerifyPinCode.Name = "txtVerifyPinCode";
            this.txtVerifyPinCode.PasswordChar = '*';
            this.txtVerifyPinCode.Size = new System.Drawing.Size(187, 26);
            this.txtVerifyPinCode.TabIndex = 8;
            this.txtVerifyPinCode.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtVerifyPinCode_KeyPress);
            // 
            // lblVerifyNewStaffPinCode
            // 
            this.lblVerifyNewStaffPinCode.AutoSize = true;
            this.lblVerifyNewStaffPinCode.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblVerifyNewStaffPinCode.Location = new System.Drawing.Point(103, 202);
            this.lblVerifyNewStaffPinCode.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblVerifyNewStaffPinCode.Name = "lblVerifyNewStaffPinCode";
            this.lblVerifyNewStaffPinCode.Size = new System.Drawing.Size(109, 17);
            this.lblVerifyNewStaffPinCode.TabIndex = 7;
            this.lblVerifyNewStaffPinCode.Text = "Verify Pin Code:";
            // 
            // txtLastName
            // 
            this.txtLastName.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            this.txtLastName.Location = new System.Drawing.Point(216, 76);
            this.txtLastName.Margin = new System.Windows.Forms.Padding(2);
            this.txtLastName.MaxLength = 50;
            this.txtLastName.Name = "txtLastName";
            this.txtLastName.Size = new System.Drawing.Size(187, 26);
            this.txtLastName.TabIndex = 3;
            // 
            // lblLastName
            // 
            this.lblLastName.AutoSize = true;
            this.lblLastName.Font = new System.Drawing.Font("Microsoft Sans Serif", 10F);
            this.lblLastName.Location = new System.Drawing.Point(132, 82);
            this.lblLastName.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblLastName.Name = "lblLastName";
            this.lblLastName.Size = new System.Drawing.Size(80, 17);
            this.lblLastName.TabIndex = 2;
            this.lblLastName.Text = "Last Name:";
            // 
            // btnCancel
            // 
            this.btnCancel.BackColor = System.Drawing.Color.Red;
            this.btnCancel.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F);
            this.btnCancel.Location = new System.Drawing.Point(316, 254);
            this.btnCancel.Margin = new System.Windows.Forms.Padding(2);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(134, 38);
            this.btnCancel.TabIndex = 10;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = false;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // btnSaveNewStaff
            // 
            this.btnSaveNewStaff.BackColor = System.Drawing.Color.Lime;
            this.btnSaveNewStaff.Font = new System.Drawing.Font("Microsoft PhagsPa", 10F);
            this.btnSaveNewStaff.Location = new System.Drawing.Point(130, 254);
            this.btnSaveNewStaff.Margin = new System.Windows.Forms.Padding(2);
            this.btnSaveNewStaff.Name = "btnSaveNewStaff";
            this.btnSaveNewStaff.Size = new System.Drawing.Size(134, 38);
            this.btnSaveNewStaff.TabIndex = 9;
            this.btnSaveNewStaff.Text = "Save New Staff";
            this.btnSaveNewStaff.UseVisualStyleBackColor = false;
            this.btnSaveNewStaff.Click += new System.EventHandler(this.btnSaveNewStaff_Click);
            // 
            // lblPinCodeRequirements
            // 
            this.lblPinCodeRequirements.AutoSize = true;
            this.lblPinCodeRequirements.ForeColor = System.Drawing.Color.Red;
            this.lblPinCodeRequirements.Location = new System.Drawing.Point(213, 121);
            this.lblPinCodeRequirements.Name = "lblPinCodeRequirements";
            this.lblPinCodeRequirements.Size = new System.Drawing.Size(137, 13);
            this.lblPinCodeRequirements.TabIndex = 4;
            this.lblPinCodeRequirements.Text = "Pin Code must be 3-6 Digits";
            // 
            // NewStaffForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(556, 332);
            this.Controls.Add(this.lblPinCodeRequirements);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnSaveNewStaff);
            this.Controls.Add(this.txtLastName);
            this.Controls.Add(this.lblLastName);
            this.Controls.Add(this.txtVerifyPinCode);
            this.Controls.Add(this.lblVerifyNewStaffPinCode);
            this.Controls.Add(this.txtPinCode);
            this.Controls.Add(this.lblNewStaffPinCode);
            this.Controls.Add(this.txtFirstName);
            this.Controls.Add(this.lblFirstName);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
            this.Name = "NewStaffForm";
            this.Text = "New Staff";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox txtFirstName;
        private System.Windows.Forms.Label lblFirstName;
        private System.Windows.Forms.TextBox txtPinCode;
        private System.Windows.Forms.Label lblNewStaffPinCode;
        private System.Windows.Forms.TextBox txtVerifyPinCode;
        private System.Windows.Forms.Label lblVerifyNewStaffPinCode;
        private System.Windows.Forms.TextBox txtLastName;
        private System.Windows.Forms.Label lblLastName;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Button btnSaveNewStaff;
        private System.Windows.Forms.Label lblPinCodeRequirements;
    }
}