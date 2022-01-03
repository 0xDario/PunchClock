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
    public partial class PunchClockForm : Form
    {
        public PunchClockForm()
        {
            InitializeComponent();
        }

        // declarations to allow use to instantiate the same form.
        private ChangePasswordForm changePasswordForm = new ChangePasswordForm();
        private NewStaffForm newStaffForm = new NewStaffForm();

        private void btnChangePassword_Click(object sender, EventArgs e)
        {
            if (changePasswordForm.IsDisposed)
            {
                changePasswordForm = new ChangePasswordForm();
                changePasswordForm.Show();
            }
            else if (!changePasswordForm.Visible)
            {
                changePasswordForm.Show();
            }
            else
            {
                changePasswordForm.BringToFront();
            }
        }

        private void btnNewStaff_Click(object sender, EventArgs e)
        {
            if (newStaffForm.IsDisposed)
            {
                newStaffForm = new NewStaffForm();
                newStaffForm.Show();
            }
            else if (!newStaffForm.Visible)
            {
                newStaffForm.Show();
            }
            else
            {
                newStaffForm.BringToFront();
            }
        }

        private void btnPunchInOut_Click(object sender, EventArgs e)
        {
            int employeeId = Convert.ToInt32(cboEmployees.SelectedValue);

            if (employeeId == 0)
            {
                MessageBox.Show("You must select an employee in order to Punch In/Out", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DateTime currentTime = DateTime.Now;

            PunchClockDataSet1TableAdapters.EmployeeTableAdapter employeeTableAdapter = new PunchClockDataSet1TableAdapters.EmployeeTableAdapter();

            if (txtPinCode.TextLength < 1)
            {
                MessageBox.Show("You must enter your 3-6 digit Pin Code to Punch In", "Incomplete Form", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int pinCodeSubmitted = Convert.ToInt32(txtPinCode.Text);

            PunchClockDataSet1TableAdapters.ShiftTableAdapter shiftTableAdapter = new PunchClockDataSet1TableAdapters.ShiftTableAdapter();

            int pinCodeStored = (int)employeeTableAdapter.GetEmployeePinCode(employeeId);

            if(pinCodeSubmitted != pinCodeStored)
            {
                MessageBox.Show("Incorrect Pin Code", "Invalid Form", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // if returned value is a 0 then the employee's last shift action was Punching In, if 1 then the employee's last shift action was Punching Out
            int lastShiftRecordId = (int)shiftTableAdapter.GetLastShiftID(employeeId);
            int isEmployeePunchedOut = (int)shiftTableAdapter.GetLastShiftRecord(employeeId);

            if (isEmployeePunchedOut == 0)
            {
                shiftTableAdapter.PunchOut(currentTime,lastShiftRecordId);
                MessageBox.Show("You have punched out at " + currentTime.ToString("dddd, dd MMMM yyyy hh:mm tt"), "Punch Out", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            else if(isEmployeePunchedOut == 1)
            {
                MessageBox.Show("You have punched in at " + currentTime.ToString("dddd, dd MMMM yyyy hh:mm tt"), "Punch In", MessageBoxButtons.OK, MessageBoxIcon.Information);
                shiftTableAdapter.Insert(employeeId, currentTime, null);
            }

            txtPinCode.Text = "";
            cboEmployees.SelectedItem = null;
            cboEmployees.SelectedText = "--Select A Staff Member--";
            btnPunchInOut.BackColor = Color.Gray;
            btnPunchInOut.Text = "";
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Set current Date + Time on Punch Clock Form
            lblDate.Text = DateTime.Now.ToString("dddd MMMM dd, yyyy");
            timer1_Tick(sender,e);

            // TODO: This line of code loads data into the 'punchClockDataSet.Employee' table. You can move, or remove it, as needed.
            this.employeeTableAdapter.Fill(this.punchClockDataSet.Employee);

            cboEmployees.SelectedItem = null;
            cboEmployees.SelectedText = "--Select A Staff Member--";

            // Timer for the clock on form
            System.Timers.Timer timer = new System.Timers.Timer();
            timer.Interval = 5000;
            timer.Elapsed += Five_Seconds_Elapsed;
            timer.Start();

            // Timer for the date on form
            System.Timers.Timer timer1 = new System.Timers.Timer();
            timer1.Interval = 60000;
            timer1.Elapsed += Minute_Elapsed;
            timer1.Start();
        }

        private void Minute_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //Update label in multiple thread
            Invoke(new MethodInvoker(delegate ()
            {
                lblDate.Text = DateTime.Now.ToString("dddd MMMM dd, yyyy");
            }));
        }

        private void Five_Seconds_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //Update label in multiple thread
            Invoke(new MethodInvoker(delegate ()
            {
                lblClock.Text = DateTime.Now.ToString("t");
            }));
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            lblClock.Text = DateTime.Now.ToString("t");
        }

        private void txtPinCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
        }

        private void employeeLastActionUpdate()
        {
            int employeeId = Convert.ToInt32(cboEmployees.SelectedValue);

            PunchClockDataSet1TableAdapters.ShiftTableAdapter shiftTableAdapter = new PunchClockDataSet1TableAdapters.ShiftTableAdapter();

            // if returned value is a 0 then the employee's last shift action was Punching In, if 1 then the employee's last shift action was Punching Out

            int isEmployeeLastActionPunchedOut = (int)shiftTableAdapter.GetLastShiftRecord(employeeId);

            if (isEmployeeLastActionPunchedOut == 0) // no
            {
                btnPunchInOut.Text = "Punch Out";
                btnPunchInOut.BackColor = Color.Red;
            }

            else if (isEmployeeLastActionPunchedOut == 1) // yes
            {
                btnPunchInOut.Text = "Punch In";
                btnPunchInOut.BackColor = Color.Green;
            }
        }

        private void cboEmployees_SelectionChangeCommitted(object sender, EventArgs e)
        {
            employeeLastActionUpdate();
        }
    }
}
