using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PunchClock
{
    public partial class ChangePasswordForm : Form
    {
        public ChangePasswordForm()
        {
            InitializeComponent();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void txtOldPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }

        private void txtNewPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }

        private void txtVerifyNewPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }

        private void btnUpdateStaffPassword_Click(object sender, EventArgs e)
        {
            if(cboEmployees.SelectedValue == null)
            {
                MessageBox.Show("You must select a staff member!", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if(txtOldPinCode.Text == "")
            {
                MessageBox.Show("You must enter your old pin code!", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int employeeId = Convert.ToInt32(cboEmployees.SelectedValue);

            PunchClockDataSet1TableAdapters.EmployeeTableAdapter employeeTableAdapter = new PunchClockDataSet1TableAdapters.EmployeeTableAdapter();
            int pinCodeStored = (int)employeeTableAdapter.GetEmployeePinCode(employeeId);

            if (Convert.ToInt32(txtOldPinCode.Text) != pinCodeStored)
            {
                MessageBox.Show("Old pin code doesn't match what is recorded in the database!", "Invalid Form", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (txtNewPinCode.Text == "")
            {
                MessageBox.Show("You must enter a new pin code!", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (txtNewPinCode.Text.Length < 3)
            {
                MessageBox.Show("New Pin code is too short!", "Invalid Form", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (txtNewPinCode.Text != txtVerifyNewPinCode.Text)
            {
                MessageBox.Show("New Pin codes do not match!", "Invalid Form", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show("You have successfully changed your pin code", "Pin Code Changed", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // update employee data record - Pin Code
            employeeTableAdapter.UpdateEmployeePinCode(Convert.ToInt32(txtNewPinCode.Text), employeeId);

            // close form
            this.Close();
        }

        private void ChangePasswordForm_Load(object sender, EventArgs e)
        {
            // TODO: This line of code loads data into the 'PunchClockDataSet.Shift' table. You can move, or remove it, as needed.
            this.shiftTableAdapter.Fill(this.PunchClockDataSet.Shift);
            this.employeeTableAdapter.Fill(this.PunchClockDataSet.Employee);
        }
    }
}
