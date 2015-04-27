﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MuMech
{
    public class MechJebModuleRCSController : ComputerModule
    {
        public Vector3d targetVelocity = Vector3d.zero;

        public PIDControllerV2 pid;
        Vector3d lastAct = Vector3d.zero;
        Vector3d worldVelocityDelta = Vector3d.zero;
        Vector3d prev_worldVelocityDelta = Vector3d.zero;

        enum ControlType
        {
            TARGET_VELOCITY,
            VELOCITY_ERROR,
            VELOCITY_TARGET_REL,
            POSITION_TARGET_REL
        };

        ControlType controlType;

        [Persistent(pass = (int)(Pass.Global))]
        [ToggleInfoItem("Conserve RCS fuel", InfoItem.Category.Thrust)]
        public bool conserveFuel = false;

        [EditableInfoItem("Conserve RCS fuel threshold", InfoItem.Category.Thrust, rightLabel = "m/s")]
        public EditableDouble conserveThreshold = 0.05;

        [Persistent(pass = (int)(Pass.Local| Pass.Type | Pass.Global))]
        [EditableInfoItem("RCS Tf", InfoItem.Category.Thrust)]
        public EditableDouble Tf = 1;

        [Persistent(pass = (int)(Pass.Global))]
        [ToggleInfoItem("RCS throttle when 0kn thrust", InfoItem.Category.Thrust)]
        public bool rcsThrottle = true;

        public MechJebModuleRCSController(MechJebCore core)
            : base(core)
        {
            priority = 600;
            pid = new PIDControllerV2(0, 0, 0, 1, -1);
        }

        public override void OnModuleEnabled()
        {
            setPIDParameters();
            pid.Reset();
            lastAct = Vector3d.zero;
            worldVelocityDelta = Vector3d.zero;
            prev_worldVelocityDelta = Vector3d.zero;
            controlType = ControlType.VELOCITY_ERROR;
            base.OnModuleEnabled();
        }

        public bool rcsDeactivate()
        {
            users.Clear();
            return true;
        }

        public void setPIDParameters()
        {
            if (Tf < 2 * TimeWarp.fixedDeltaTime)
                Tf = 2 * TimeWarp.fixedDeltaTime;

            pid.Kd = 0.53 / Tf;
            pid.Kp = pid.Kd / (3 * Math.Sqrt(2) * Tf);
            pid.Ki = pid.Kp / (12 * Math.Sqrt(2) * Tf);
        }


        // When evaluating how fast RCS can accelerate and calculate a speed that available thrust should
        // be multiplied by that since the PID controller actual lower the used acceleration
        public double rcsAccelFactor()
        {
            return pid.Kp;
        }

        public override void OnModuleDisabled()
        {
            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            base.OnModuleDisabled();
        }

        public void SetTargetWorldVelocity(Vector3d vel)
        {
            targetVelocity = vel;
            controlType = ControlType.TARGET_VELOCITY;
        }

        public void SetWorldVelocityError(Vector3d dv)
        {
            worldVelocityDelta = -dv;
            if (controlType != ControlType.VELOCITY_ERROR)
            {
                prev_worldVelocityDelta = worldVelocityDelta;
                controlType = ControlType.VELOCITY_ERROR;
            }
        }

        public void SetTargetRelative(Vector3d vel)
        {
            targetVelocity = vel;
            controlType = ControlType.VELOCITY_TARGET_REL;
        }

        public override void Drive(FlightCtrlState s)
        {
            setPIDParameters();

            switch (controlType)
            {
                case ControlType.TARGET_VELOCITY:
                    // Removed the gravity since it also affect the target and we don't know the target pos here.
                    // Since the difference is negligable for docking it's removed
                    // TODO : add it back once we use the RCS Controler for other use than docking. Account for current acceleration beside gravity ?
                    worldVelocityDelta = vesselState.orbitalVelocity - targetVelocity;
                    //worldVelocityDelta += TimeWarp.fixedDeltaTime * vesselState.gravityForce; //account for one frame's worth of gravity
                    //worldVelocityDelta -= TimeWarp.fixedDeltaTime * gravityForce = FlightGlobals.getGeeForceAtPosition(  Here be the target position  ); ; //account for one frame's worth of gravity
                    break;

                case ControlType.VELOCITY_ERROR:
                    // worldVelocityDelta already contains the velocity error
                    break;

                case ControlType.VELOCITY_TARGET_REL:
                    if (core.target.Target == null)
                    {
                        rcsDeactivate();
                        return;
                    }

                    worldVelocityDelta = core.target.RelativeVelocity - targetVelocity;
                    break;
            }

            // We work in local vessel coordinate
            Vector3d velocityDelta = Quaternion.Inverse(vessel.GetTransform().rotation) * worldVelocityDelta;

            if (!conserveFuel || (velocityDelta.magnitude > conserveThreshold))
            {
                if (!vessel.ActionGroups[KSPActionGroup.RCS])
                {
                    vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                }

                Vector3d rcs = new Vector3d();

                for (int i = 0; i < Vector6.Values.Length; i++)
                {
                    Vector6.Direction dir = Vector6.Values[i];
                    double dirDv = Vector3d.Dot(velocityDelta, Vector6.directions[(int)dir]);
                    double dirAvail = vesselState.rcsThrustAvailable[dir]; 
                    if (dirAvail  > 0 && Math.Abs(dirDv) > 0.001)
                    {
                        double dirAction = dirDv / (dirAvail * TimeWarp.fixedDeltaTime / vesselState.mass);
                        if (dirAction > 0)
                        {
                            rcs += Vector6.directions[(int)dir] * dirAction;
                        }
                    }
                }
                                
                Vector3d omega = Vector3d.zero;

                switch (controlType)
                {
                    case ControlType.TARGET_VELOCITY:
                        omega = Quaternion.Inverse(vessel.GetTransform().rotation) * (vessel.acceleration - vesselState.gravityForce);
                        break;

                    case ControlType.VELOCITY_TARGET_REL:
                    case ControlType.VELOCITY_ERROR:
                        omega = (worldVelocityDelta - prev_worldVelocityDelta) / TimeWarp.fixedDeltaTime;
                        prev_worldVelocityDelta = worldVelocityDelta;
                        break;
                }

                rcs = pid.Compute(rcs, omega);

                // Disabled the low pass filter for now. Was doing more harm than good
                //rcs = lastAct + (rcs - lastAct) * (1 / ((Tf / TimeWarp.fixedDeltaTime) + 1));
                lastAct = rcs;

                s.X = Mathf.Clamp((float)rcs.x, -1, 1);
                s.Y = Mathf.Clamp((float)rcs.z, -1, 1); //note that z and
                s.Z = Mathf.Clamp((float)rcs.y, -1, 1); //y must be swapped
            }
            else if (conserveFuel)
            {
                if (vessel.ActionGroups[KSPActionGroup.RCS])
                {
                    vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                }
            }

            base.Drive(s);
        }
    }
}
