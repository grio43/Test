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
    public partial class RoomAnalyzerForm : Form
    {
        public RoomAnalyzerForm()
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
                    var entities = AbyssalController.GetSortedTargetList(abyssController.TargetsOnGridWithoutLootTargets);

                    var res = allDrones.Select(n => new
                    {
                        n.DroneId,
                        FollowEnt = abyssController.allDronesInSpace.Where(e => e.Id ==  n.DroneId).FirstOrDefault()?.FollowEntity?.TypeName ?? "No Follow entity",
                        n.TypeName,
                        n.ActionState,
                        EXP_DPS = Math.Round(n.GetInvType?.GetDroneDPS()[DirectDamageType.EXPLO] ?? 0, 2),
                        KIN_DPS = Math.Round(n.GetInvType?.GetDroneDPS()[DirectDamageType.KINETIC] ?? 0, 2),
                        EM_DPS = Math.Round(n.GetInvType?.GetDroneDPS()[DirectDamageType.EM] ?? 0, 2),
                        TRM_DPS = Math.Round(n.GetInvType?.GetDroneDPS()[DirectDamageType.THERMAL] ?? 0, 2),
                        SPAWN_DPS = Math.Round(DirectEntity.CalculateEffectiveDPS(n.GetInvType?.GetDroneDPS() ?? new Dictionary<DirectDamageType, float>(), entities), 2),
                        SPAWN_DPS_BW = Math.Round(DirectEntity.CalculateEffectiveDPS(n.GetInvType?.GetDroneDPS() ?? new Dictionary<DirectDamageType, float>(), entities) / n.Bandwidth, 2)
                        //src = n
                    }).ToList();

                    var dt = Util.ConvertToDataTable(res);
                    Invoke(new Action(() =>
                    {
                        dataGridView1.DataSource = null;
                        dataGridView1.Rows.Clear();
                        dataGridView1.Columns.Clear();
                        dataGridView1.Refresh();
                        dataGridView1.DataSource = dt;

                    }));

                    var res2 = entities.Select(n => new
                    {
                        n.Id,
                        n.TypeName,
                        n.TypeId,
                        MAX_DPS = n.GetMaxDPSFrom(),
                        CURR_DPS = n.GetCurrentDPSFrom(),
                        ExpEHP = Math.Round(n.ExpEHP ?? 0, 2),
                        KinEHP = Math.Round(n.KinEHP ?? 0, 2),
                        EmEHP = Math.Round(n.EmEHP ?? 0, 2),
                        TrmEHP = Math.Round(n.TrmEHP ?? 0, 2),
                        n.AbyssalTargetPriority
                        //src = n
                    }).ToList();

                    var dt2 = Util.ConvertToDataTable(res2);
                    Invoke(new Action(() =>
                    {
                        dataGridView2.DataSource = null;
                        dataGridView2.Rows.Clear();
                        dataGridView2.Columns.Clear();
                        dataGridView2.Refresh();
                        dataGridView2.DataSource = dt2;

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

        private void dataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }

        private void dataGridView1_SelectionChanged(object sender, EventArgs e)
        {
            dynamic obj = null;
            try
            {
                obj = dataGridView1.CurrentRow.DataBoundItem;
            }
            catch (Exception exception)
            {
                Log.WriteLine(exception.ToString());
            }

            ModifyButtons();
            var waitUntil = DateTime.MinValue;
            ActionQueueAction action = null;
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
                    var entities = abyssController.TargetsOnGridWithoutLootTargets;

                    var dronesInSpace = abyssController.allDronesInSpace.Select(d => new AbyssalDrone(d)).ToList();
                    var dronesInBay = abyssController.alldronesInBay.Select(d => new AbyssalDrone(d)).ToList();
                    var allDrones = dronesInSpace.Concat(dronesInBay).ToList();

                    var selectedDroneId = (long)dataGridView1?.CurrentRow?.Cells["DroneId"]?.Value;
                    var selectedEntityId = 0L;

                    try
                    {
                        selectedEntityId = (long)dataGridView2?.CurrentRow?.Cells["Id"]?.Value;
                    }
                    catch { }

                    var selectedDrone = allDrones.FirstOrDefault(d => d.DroneId == selectedDroneId);

                    if (selectedDrone == null)
                    {
                        Console.WriteLine("Selected drone is null.");
                    }

                    var secondsToKillEntireGrid = entities.Sum(e => e.GetSecondsToKill(selectedDrone.GetInvType?.GetDroneDPS(), out _));



                    Dictionary<DirectDamageType, float> dict = new Dictionary<DirectDamageType, float>();
                    foreach (var drone in dronesInSpace)
                    {
                        foreach (var kv in drone.GetInvType?.GetDroneDPS())
                        {
                            if (dict.ContainsKey(kv.Key))
                            {
                                dict[kv.Key] += kv.Value;
                            }
                            else
                            {
                                dict.Add(kv.Key, kv.Value);
                            }
                        }
                    }
                    var effDPs = 0d;
                    var secondsToKillActiveDrones = entities.Sum(e => e.GetSecondsToKill(dict, out effDPs));

                    var secondsToKillSelectedEntity = 0d;

                    var selectedEntity = entities.FirstOrDefault(e => e.Id == selectedEntityId);
                    if (selectedEntity != null)
                    {
                        secondsToKillSelectedEntity = selectedEntity.GetSecondsToKill(selectedDrone.GetInvType?.GetDroneDPS(), out _);
                    }

                    var res = new[] { new { SecsKillEntireGridWithSelectedDrone = Math.Round(secondsToKillEntireGrid, 2),
                                            SecsKillSelectEntWithSelectedDrone = Math.Round(secondsToKillSelectedEntity, 2),
                                            SecondsToKillGridWithActiveDrones = Math.Round(secondsToKillActiveDrones, 2),
                                            DronesInSpaceDPS  = Math.Round(effDPs, 2),

                    } };

                    Task.Run(() =>
                    {
                        return Invoke(new Action(() =>
                        {
                            dataGridView3.DataSource = res;
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
    }
}
