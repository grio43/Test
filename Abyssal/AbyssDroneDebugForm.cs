extern alias SC;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Framework;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SC::SharedComponents.Utility;
using static EVESharpCore.Controllers.Abyssal.AbyssalController;

namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssDroneDebugForm : Form
    {
        public AbyssDroneDebugForm()
        {
            InitializeComponent();
        }

        private void ModifyButtons(bool enabled = false)
        {
            Invoke(new Action(() =>
            {
                foreach (var b in Controls)
                    if (b is Button button)
                        button.Enabled = enabled;
            }));
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ModifyButtons();
            var waitUntil = DateTime.MinValue;
            ActionQueueAction action = null;

            //Type dgvType = dataGridView1.GetType();
            //PropertyInfo pi = dgvType.GetProperty("DoubleBuffered",
            //    BindingFlags.Instance | BindingFlags.NonPublic);
            //pi.SetValue(dataGridView1, true, null);

            action = new ActionQueueAction(new Action(() =>
            {
                try
                {
                    if (waitUntil > DateTime.UtcNow)
                    {
                        action.QueueAction();
                        return;
                    }

                    var abyssController = ControllerManager.Instance.GetController<AbyssalController>();

                    var dronesInSpace = abyssController.allDronesInSpace.Select(d => new AbyssalDrone(d)).ToList();
                    var dronesInBay = abyssController.alldronesInBay.Select(d => new AbyssalDrone(d)).ToList();
                    var allDrones = dronesInSpace.Concat(dronesInBay).ToList();

                    var res = allDrones.Select(n => new
                    {
                        n.DroneId,
                        n.TypeName,
                    }).ToList();

                    var dt = Util.ConvertToDataTable(res);
                    Invoke(new Action(() =>
                    {
                        dataGridView1.DataSource = null;
                        dataGridView1.Rows.Clear();
                        dataGridView1.Columns.Clear();
                        dataGridView1.Refresh();
                        dataGridView1.DataSource = dt;
                        //dataGridView1.AutoGenerateColumns = false;
                        // Add a DataGridViewCheckBoxColumn for the bool column
                        DataGridViewCheckBoxColumn boolColumn = new DataGridViewCheckBoxColumn();
                        //boolColumn.DataPropertyName = "ForceLowLife"; // Replace with the actual property name
                        boolColumn.HeaderText = "Bool Column";
                        boolColumn.Name = "BoolColumn";
                        dataGridView1.Columns.Add(boolColumn);


                        dataGridView1.CellContentClick += (sender, e) =>
                        {
                            if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
                            {
                                DataGridViewCell cell = dataGridView1.Rows[e.RowIndex].Cells[e.ColumnIndex];

                                if (cell.OwningColumn.Name == "BoolColumn" && cell is DataGridViewCheckBoxCell)
                                {
                                    var id = (long)cell.OwningRow.Cells["DroneId"].Value;
                                    var c = (DataGridViewCheckBoxCell)cell;
                                    if (Convert.ToBoolean(c.Value) != true)
                                    {
                                        Console.WriteLine($"Setting health override for droneId [{id}] (0.1d, 0.1d, 0.1d)");
                                        DirectEve._entityHealthPercOverrides[id] = (0.1d, 0.1d, 0.1d);
                                        c.Value = true;
                                    }
                                    else
                                    {
                                        if (DirectEve._entityHealthPercOverrides.ContainsKey(id))
                                        {
                                            Console.WriteLine($"Removed health override for droneId [{id}]");
                                            DirectEve._entityHealthPercOverrides.Remove(id);
                                        }
                                        c.Value = false;
                                    }
                                    dataGridView1.EndEdit(); // Commit the change
                                }
                            }
                        };
                    }));

                    Task.Run(() =>
                    {
                        return Invoke(new Action(() =>
                        {
                            ModifyButtons(true);
                        }));
                    });
                }
                catch (Exception ex)
                {
                    Log.WriteLine(ex.ToString());
                }
            }));
            action.Initialize().QueueAction();
        }

        private void button3_Click(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            DirectEve._entityHealthPercOverrides = new System.Collections.Generic.Dictionary<long, (double, double, double)>();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {


        }

        private void AbyssDroneDebugForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            DirectEve._entityHealthPercOverrides = new System.Collections.Generic.Dictionary<long, (double, double, double)>();
            var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
            abyssController.DroneDebugState = false;
        }

        private void AbyssDroneDebugForm_Shown(object sender, EventArgs e)
        {
            //var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
            //abyssController.DroneDebugState = true;
            //comboBox1.DataSource = Enum.GetValues(typeof(AbyssalRoomStrategy));
            //comboBox1.SelectedItem = abyssController.RoomStrategy;
        }

        //private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        //{
        //    var abyssController = ControllerManager.Instance.GetController<AbyssalController>();
        //    abyssController.RoomStrategy = (AbyssalRoomStrategy)comboBox1.SelectedItem;
        //}
    }
}
