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
                MessageBox.Show("You must select a staff member!");
                return;
            }

            if(txtOldPinCode.Text == "")
            {
                MessageBox.Show("You must enter your old pin code!");
                return;
            }

            if (txtNewPinCode.Text == "")
            {
                MessageBox.Show("You must enter a new pin code!");
                return;
            }

            if (txtNewPinCode.Text.Length < 3)
            {
                MessageBox.Show("New Pin code is too short!");
                return;
            }

            if (txtNewPinCode.Text != txtVerifyNewPinCode.Text)
            {
                MessageBox.Show("Pin codes do not match!");
                return;
            }

            if (txtOldPinCode.Text != "password from db")
            {
                MessageBox.Show("Old pin code doesn't match what is recorded in the database!");
                return;
            }
        }

        private void ChangePasswordForm_Load(object sender, EventArgs e)
        {
            // TODO: This line of code loads data into the 'savoiaPunchClockDataSet.Employee' table. You can move, or remove it, as needed.
            this.employeeTableAdapter.Fill(this.savoiaPunchClockDataSet.Employee);
        }
    }
}
