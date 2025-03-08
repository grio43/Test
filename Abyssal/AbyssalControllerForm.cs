using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Controllers.Questor.Core.Activities;
using EVESharpCore.Framework;

namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssalControllerForm : Form
    {
        #region Fields

        private AbyssalBaseController _controller;

        #endregion Fields

        #region Constructors

        public AbyssalControllerForm(AbyssalBaseController c)
        {
            this._controller = c;
            InitializeComponent();
        }

        #endregion Constructors

        #region Methods

        public DataGridView GetDataGridView1 => this.dataGridView1;

        public Label IskPerHLabel => this.label2;

        public Label StageLabel => this.label3;

        public Label StageRemainingSeconds => this.label5;

        public Label EstimatedNpcKillTime => this.label7;

        public Label AbyssTotalTime => this.label13;

        public Label WreckLootTime => this.label15;

        public Label TimeNeededToGetToTheGate => this.label17;

        public Label TotalStageEhp => this.label11;

        public Label IgnoreAbyssEntities => this.label10;


        #endregion Methods

        private void label6_Click(object sender, System.EventArgs e)
        {

        }

        private void label10_Click(object sender, System.EventArgs e)
        {

        }

        private void label9_Click(object sender, System.EventArgs e)
        {

        }

        private void label12_Click(object sender, System.EventArgs e)
        {

        }

        private void label11_Click(object sender, System.EventArgs e)
        {

        }

        private void label5_Click(object sender, System.EventArgs e)
        {

        }

        private void button1_Click(object sender, System.EventArgs e)
        {


        }

        private void button2_Click(object sender, System.EventArgs e)
        {

        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {


        }

        private void button3_Click(object sender, EventArgs e)
        {

        }

        private void button3_Click_1(object sender, EventArgs e)
        {
            new AbyssDroneDebugForm().Show();
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            new RoomAnalyzerForm().Show();
        }

        private void button2_Click_1(object sender, EventArgs e)
        {
            ActionQueueAction.Run(() =>
            {
                var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
                if (abyssController != null)
                {
                    abyssController.State = AbyssalState.PVP;
                    DirectMe.InvulnOverrideUntil = DateTime.UtcNow.AddSeconds(28);
                }
            });
        }

        private void button4_Click(object sender, EventArgs e)
        {
            ActionQueueAction.Run(() =>
            {
                var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
                if (abyssController != null)
                {
                    DirectMe.InvulnOverrideUntil = DateTime.MinValue;
                }
            });
        }

        private void button5_Click(object sender, EventArgs e)
        {
            var form = this;
            ActionQueueAction.Run(() =>
            {
                var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
                if (abyssController != null)
                {
                    var newState = !abyssController.ManualPause;
                    abyssController.ManualPause = newState;

                    Task.Run(() =>
                    {
                        form.TogglePauseButton.Invoke(new Action(() =>
                        {
                            form.TogglePauseButton.BackColor = newState 
                                ? System.Drawing.Color.Red 
                                : System.Drawing.Color.Green;
                        }));
                    });
                }
            });
        }
    }
}