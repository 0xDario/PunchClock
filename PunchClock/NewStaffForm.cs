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
            PunchClockDataSet1TableAdapters.EmployeeTableAdapter employeeTableAdapter = new PunchClockDataSet1TableAdapters.EmployeeTableAdapter();
            PunchClockDataSet1TableAdapters.ShiftTableAdapter shiftTableAdapter = new PunchClockDataSet1TableAdapters.ShiftTableAdapter();

            // add employee
            employeeTableAdapter.Insert(txtFirstName.Text, txtLastName.Text, Convert.ToInt32(txtPinCode.Text));

            int recentlyAddedEmployeeID = (int)employeeTableAdapter.GetLatestEmployeeID();

            // insert record into shift table to so last shift action query functions properly without null results
            shiftTableAdapter.Insert(recentlyAddedEmployeeID, DateTime.Now, DateTime.Now);

            MessageBox.Show("Staff member '" +txtFirstName.Text + " " + txtLastName.Text + "' has been successfully added into the system.\n\nPlease restart the application in order for your changes to take effect.","New Employee Added!", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
