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
            DateTime currentTime = DateTime.Now;
            
            SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter employeeTableAdapter = new SavoiaPunchClockDataSetTableAdapters.EmployeeTableAdapter();

            if (txtPinCode.TextLength < 1)
            {
                MessageBox.Show("You must enter your 3-6 digit Pin Code to Punch In");
                return;
            }

            int employeeId = Convert.ToInt32(cboEmployees.SelectedValue);

            if (employeeId == 0)
            {
                MessageBox.Show("You must select an employee in order to Punch In");
                return;
            }

            int pinCodeSubmitted = Convert.ToInt32(txtPinCode.Text);

            SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter shiftTableAdapter = new SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter();

            int pinCodeStored = (int)employeeTableAdapter.GetEmployeePinCode(employeeId);

            if(pinCodeSubmitted != pinCodeStored)
            {
                MessageBox.Show("Incorrect Pin Code");
                return;
            }

            // if returned value is a 0 then the employee's last shift action was Punching In, if 1 then the employee's last shift action was Punching Out
            int lastShiftRecordId = (int)shiftTableAdapter.GetLastShiftID(employeeId);
            int isEmployeePunchedOut = (int)shiftTableAdapter.GetLastShiftRecord(employeeId);

            if (isEmployeePunchedOut == 0)
            {
                shiftTableAdapter.PunchOut(currentTime,lastShiftRecordId);
                MessageBox.Show("You have punched out at " + currentTime);
            }

            else if(isEmployeePunchedOut == 1)
            {
                MessageBox.Show("You have clocked in at " + currentTime);
                shiftTableAdapter.Insert(employeeId, currentTime, null);
            }

            txtPinCode.Text = "";
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Set current Date + Time on Punch Clock Form
            lblDate.Text = DateTime.Now.ToString("dddd MMMM dd, yyyy");
            timer1_Tick(sender,e);

            // TODO: This line of code loads data into the 'savoiaPunchClockDataSet.Employee' table. You can move, or remove it, as needed.
            this.employeeTableAdapter.Fill(this.savoiaPunchClockDataSet.Employee);

            employeeLastActionUpdate();

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

            SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter shiftTableAdapter = new SavoiaPunchClockDataSetTableAdapters.ShiftTableAdapter();

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

        private void cboEmployees_SelectedIndexChanged(object sender, EventArgs e)
        {
            employeeLastActionUpdate();
        }
    }
}
