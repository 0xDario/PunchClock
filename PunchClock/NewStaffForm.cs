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
    public partial class NewStaffForm : Form
    {
        public NewStaffForm()
        {
            InitializeComponent();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnSaveNewStaff_Click(object sender, EventArgs e)
        {
            if(txtFirstName.Text == "" || txtLastName.Text == "")
            {
                MessageBox.Show("First and last name cannot be blank", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if(txtPinCode.Text == "")
            {
                MessageBox.Show("Pin code cannot be blank", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if(txtPinCode.Text.Length < 3)
            {
                MessageBox.Show("Pin code is too short!", "Pin Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if(txtPinCode.Text != txtVerifyPinCode.Text)
            {
                MessageBox.Show("Pin codes do not match!", "Pin Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create new TableAdapter so we can insert row
            SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter employeeTableAdapter = new SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter();

            // add employee
            employeeTableAdapter.Insert(txtFirstName.Text, txtLastName.Text, Convert.ToInt32(txtPinCode.Text));

            MessageBox.Show("Staff member '" +txtFirstName.Text + " " + txtLastName.Text + "' has been successfully added into the system.");

            // close form
            this.Close();
        }

        private void txtPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }

        private void txtVerifyPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }
    }
}
