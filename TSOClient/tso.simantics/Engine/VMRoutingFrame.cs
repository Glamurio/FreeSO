using System;
using System.Collections.Generic;
using System.Linq;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using FSO.LotView.Model;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Components;
using FSO.Common.Utils;
using FSO.SimAntics.Engine.Routing;
using FSO.SimAntics.Model.Routing;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Diagnostics;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// Determines the path to a set destination from an avatar and provides a system to walk them there. First finds the
    /// set of room portals to get to the target's room (calls their portal functions in order), then pathfinds to the relevant 
    /// position once in the same room.
    /// 
    /// If in the same room or a flag is set to "ignore rooms", the room step is ignored and the character is directly routed 
    /// to the specified position. Pathfinders are normally pushed to the stack, but behave completely differently from
    /// stack frames. You can imagine an example case, where the avatar is currently routing to a door, but the final destination
    /// is the piano.
    /// 
    /// STACK:
    /// Private: Interaction - Play (piano)
    /// Private: route to piano and sit (piano)
    /// VMPathFinder: Go on top of, opposing direction. (this is passed as a position and direction.)
    /// Semi-global: Portal function (door)
    /// Semi-global: Goto door and gace through (door)
    /// VMPathFinder: Go on top of, same direction. (passed as position direction, but this time destination is in same room, so we call no portal functions)
    /// 
    /// Once the first portal function returns true, we will pop back to the initial VMPathFinder to route to the second or the object.
    /// This can repeat many times. 
    /// 
    /// However, if a portal function returns false, we will need to re-evaluate the portal route to the
    /// destination, discounting the route from the previous portal to the portal we failed to route to. (nodes are no longer connected) 
    /// The Sims 1 does not do this, but routing problems annoy me as much as everyone else. :)
    /// </summary>
    public class VMRoutingFrame : VMStackFrame
    {
        public static bool DEBUG_DRAW = false;

        private static ushort ROUTE_FAIL_TREE = 398;
        private static uint GOTO_GUID = 0x000007C4;
        private static short SHOO_INTERACTION = 3;
        private static ushort SHOO_TREE = 4107;

        //each within-room route gets these allowances separately.
        private static int WAIT_TIMEOUT = 10 * 30; //10 seconds
        private static int MAX_RETRIES = 5;

        //side-stepping speed through a pinch, in the same units as Velocity (full walking speed is 8).
        private const int SHIMMY_VELOCITY = 2;
        //pets squeeze through walking forwards at half speed (their move frames are already doubled).
        private const int SHIMMY_VELOCITY_PET = 4;
        //STR 150/151 (person walk animations): standing-adjust-w and standing-adjust-e
        private const int ANIM_ADJUST_LEFT = 14;
        private const int ANIM_ADJUST_RIGHT = 15;

        private Stack<VMRoomPortal> Rooms = new Stack<VMRoomPortal>();
        private VMRoomPortal CurrentPortal;

        public LinkedList<VMIPathSegment> WalkTo;
        private double WalkDirection;
        private double TargetDirection;
        private bool IgnoreRooms;

        public VMRoutingFrameState State = VMRoutingFrameState.INITIAL;
        public int PortalTurns = 0;
        public int WaitTime = 0;
        private int Timeout = WAIT_TIMEOUT;
        private int Retries = MAX_RETRIES;

        private bool AttemptedChair = false;
        private float TurnTweak = 0;
        private int TurnFrames = 0;

        private int MoveTotalFrames = 0;
        private int MoveFrames = 0;
        private int Velocity = 0;
        private VMRoutingFrame ParentRoute;

        private short WalkStyle
        {
            get
            {
                return (InPool)?(short)0:Caller.GetValue(VMStackObjectVariable.WalkStyle);
            }
            set
            {
                Caller.SetValue(VMStackObjectVariable.WalkStyle, value);
            }
        }

        private bool InPool
        {
            get
            {
                return VM.Context.RoomInfo[VM.Context.GetRoomAt(Caller.Position)].Room.IsPool;
            }
        }

        public bool CallFailureTrees = false;

        private HashSet<VMRoomPortal> IgnoredRooms = new HashSet<VMRoomPortal>();
        private HashSet<VMAvatar> AvatarsToConsider = new HashSet<VMAvatar>();

        private LotTilePos PreviousPosition;
        private VMIPathSegment CurrentPath;

        private bool RoomRouteInvalid;
        private SLOTItem Slot;
        private VMEntity Target;
        private List<VMFindLocationResult> Choices;
        private VMFindLocationResult CurRoute;
        private short LastWalkStyle = -1;

        public VMRoutingFrame() { }
        
        private void Init()
        {
            ParentRoute = GetParentFrame();
            if (ParentRoute != null)
            {
                AvatarsToConsider = new HashSet<VMAvatar>(ParentRoute.AvatarsToConsider);
            }
            else
            {
                foreach (var obj in VM.Entities)
                {
                    if (obj is VMAvatar)
                    {
                        var colAvatar = (VMAvatar)obj;
                        var colTopFrame = colAvatar.Thread.Stack.LastOrDefault();

                        if (colTopFrame != null && colTopFrame is VMRoutingFrame)
                        {
                            var colRoute = (VMRoutingFrame)colTopFrame;
                            if (colRoute.WaitTime > 0) AvatarsToConsider.Add(colAvatar);
                        }
                    }
                }
            }
        }

        public bool IntersectsOurDestination(VMFindLocationResult target)
        {
            if (CurRoute == null) return false;
            if (target.Chair != null && target.Chair == CurRoute.Chair) return true;
            var pos = CurRoute.Position;
            var tpos = target.Position;
            var obs = new VMObstacle(pos.x - 6, pos.y - 6, pos.x + 6, pos.y + 6);
            return obs.Contains(new Point(tpos.x, tpos.y));
        }

        public bool InitRoutes(SLOTItem slot, VMEntity target)
        {
            Init();

            Slot = slot;
            Target = target;
            Retargets = 0;
            TargetPosKnown = false;
            VMDiagnostics.Route(Caller, VMRouteEventType.Begin, goal: target?.Position, detail: "slot route");
            var found = AttemptRoute(null);

            if (found == VMRouteFailCode.Success) return true;
            else HardFail(found, null);
            return false;
        }

        public bool InitRoutes(List<VMFindLocationResult> choices) //returns false if we can't find a single route
        {
            Init();

            Choices = choices; //should be ordered by most preferred first, with a little bit of random shuffling to keep things interesting for "wander"
            //style movements. Also includes flags dictating if this route goes through walls etc.
            VMDiagnostics.Route(Caller, VMRouteEventType.Begin, detail: choices.Count + " goal(s)");
            var found = VMRouteFailCode.NoValidGoals;
            while (found != VMRouteFailCode.Success && Choices.Count > 0) {
                found = AttemptRoute(Choices[0]);
                Choices.RemoveAt(0);
            }

            if (found == VMRouteFailCode.Success) return true;
            else HardFail(found, null);
            return false;
        }

        public void InvalidateRoomRoute()
        {
            RoomRouteInvalid = true;
        }

        public void SoftFail(VMRouteFailCode code, VMEntity blocker)
        {
            VMDiagnostics.Route(Caller, VMRouteEventType.SoftFail, code, blocker, CurRoute?.Position,
                (Choices?.Count ?? 0) + " goal(s) left");
            if (VMFeatures.ApproachPositions && Slot != null && Choices != null && Choices.Count > 0)
            {
                //the remaining positions were chosen when the route started: check them against the lot as it is now.
                var dropped = VMSlotParser.RecheckLocations(Choices, Slot, Caller, VM.Context);
                if (dropped > 0) VMDiagnostics.Route(Caller, VMRouteEventType.Choices, goal: Target?.Position,
                    detail: dropped + " remaining position(s) now taken, " + Choices.Count + " left");
            }
            var found = VMRouteFailCode.NoValidGoals;
            while (found != VMRouteFailCode.Success && Choices != null && Choices.Count > 0)
            {
                found = AttemptRoute(Choices[0]);
                Choices.RemoveAt(0);
            }

            if (found != VMRouteFailCode.Success) HardFail(code, blocker);
            else if (VMFeatures.RoutingFixes && State != VMRoutingFrameState.INITIAL)
            {
                //the next goal may be in another room or be a chair: run it through the INITIAL state (portals, sit,
                //stand up) rather than continuing to walk, which would head straight for its position.
                State = VMRoutingFrameState.INITIAL;
            }
        }

        private void HardFail(VMRouteFailCode code, VMEntity blocker)
        {
            VMDiagnostics.Route(Caller, VMRouteEventType.HardFail, code, blocker, CurRoute?.Position);
            //remembered so object selection and free will can prefer other objects for a while. Recording alone changes
            //nothing (see VMFeatures.ObjectSelection, VMFeatures.AutonomyFixes). Failing to stand up says nothing about the target.
            if (ParentRoute == null && Target != null && code != VMRouteFailCode.CantStand) VMRouteFailMemory.Record(Caller, Target);
            State = VMRoutingFrameState.FAILED;
            var avatar = (VMAvatar)Caller;
            if (CallFailureTrees && ParentRoute == null)
            {
                var bhav = (VMRoutine)Global.Resource.GetRoutine(ROUTE_FAIL_TREE);
                Thread.ExecuteSubRoutine(this, bhav, CodeOwner, new VMSubRoutineOperand(new short[] { (short)code, (blocker==null)?(short)0:blocker.ObjectID, 0, 0 }));
            }
            avatar.SetPersonData(VMPersonDataVariable.RouteResult, (short)code);
        }

        private bool DoRoomRoute(VMFindLocationResult route)
        {
            Rooms = new Stack<VMRoomPortal>();

            LotTilePos dest;
            if (Slot != null)
            {
                //take destination pos from object. Estimate room closeness using distance to object, not destination.
                dest = Target.Position;
            }
            else
            {
                if (route != null) dest = route.Position;
                else return false; //???
            }

            var DestRoom = VM.Context.GetRoomAt(dest);
            var MyRoom = VM.Context.GetRoomAt(Caller.Position);

            IgnoreRooms = (Slot != null && (Slot.Rsflags & SLOTFlags.IgnoreRooms) > 0);

            if (DestRoom == MyRoom || IgnoreRooms) return true; //we don't have to do any room finding for this
            else
            {
                //find shortest room traversal to destination. Simple A* pathfind.
                //Portals are considered nodes to allow multiple portals between rooms to be considered.

                var openSet = new List<VMRoomPortal>(); //we use this like a queue, but we need certain functions for sorted queue that are only provided by list.
                var closedSet = new HashSet<VMRoomPortal>();

                var gScore = new Dictionary<VMRoomPortal, double>();
                var fScore = new Dictionary<VMRoomPortal, double>();
                var parents = new Dictionary<VMRoomPortal, VMRoomPortal>();

                var StartPortal = new VMRoomPortal(Caller.ObjectID, MyRoom); //consider the sim as a portal to this room (as a starting point)
                openSet.Add(StartPortal);
                gScore[StartPortal] = 0;
                fScore[StartPortal] = GetDist(Caller.Position, dest);

                while (openSet.Count != 0)
                {
                    var current = openSet[0];
                    openSet.RemoveAt(0);

                    if (current.TargetRoom == DestRoom)
                    {
                        //this portal gets us to the room.
                        while (current != StartPortal) //push previous portals till we get to our first "portal", the sim in its current room (we have already "traversed" this portal)
                        {
                            Rooms.Push(current);
                            current = parents[current];
                        }
                        return true;
                    }

                    closedSet.Add(current);

                    var portals = VM.Context.RoomInfo[current.TargetRoom].Portals;

                    foreach (var portal in portals)
                    { //evaluate all neighbor portals
                        if (IgnoredRooms.Contains(portal) || closedSet.Contains(portal)) continue; //already evaluated, or couldn't get to the portal.

                        var pos = VM.GetObjectById(portal.ObjectID).Position;
                        var gFromCurrent = gScore[current] + GetDist(VM.GetObjectById(current.ObjectID).Position, pos);
                        var newcomer = !openSet.Contains(portal);

                        if (newcomer || gFromCurrent < gScore[portal])
                        {
                            parents[portal] = current; //best parent for now
                            gScore[portal] = gFromCurrent;
                            fScore[portal] = gFromCurrent + GetDist(pos, dest);
                            if (newcomer)
                            { //add and move to relevant position
                                OpenSetSortedInsert(openSet, fScore, portal);
                            }
                            else
                            { //remove and reinsert to refresh sort
                                openSet.Remove(portal);
                                OpenSetSortedInsert(openSet, fScore, portal);
                            }
                        }
                    }
                }

                return false;
            }
        }

        private VMRouteFailCode AttemptRoute(VMFindLocationResult route) { //returns false if there is no room portal route to the destination room.
                                                                           //if route is not null, we are on a DIRECT route, where either the SLOT has been resolved or a route has already been passed to us.
                                                                           //resets some variables either way, so that the route can start again.
            if (LastWalkStyle != -1) WalkStyle = LastWalkStyle;
            CurRoute = route;

            if (Caller.PersistID > 0 && !VM.TS1 && Callee?.Object?.GUID != GOTO_GUID)
            {
                AutoWalkSpeed();
            }

            WalkTo = null; //reset routing state
            AttemptedChair = false;
            TurnTweak = 0;
            if (VMFeatures.RoutingFixes)
            {
                //each goal gets its own allowance (the field comments always said so, but nothing refilled them),
                //and must not inherit a wait from the goal that was given up.
                Timeout = WAIT_TIMEOUT;
                Retries = MAX_RETRIES;
                WaitTime = 0;
            }
            PortalRetries = 0;

            VMDiagnostics.Route(Caller, VMRouteEventType.Goal, goal: route?.Position ?? Target?.Position, other: route?.Chair);
            var roomRoute = DoRoomRoute(route);
            if (!roomRoute) VMDiagnostics.Route(Caller, VMRouteEventType.NoRoomRoute, VMRouteFailCode.NoRoomRoute, goal: route?.Position ?? Target?.Position);
            return roomRoute ? VMRouteFailCode.Success : VMRouteFailCode.NoRoomRoute;
        }

        /// <summary>
        /// Pathfinds to the destination position from the current. The room pathfind should get us to the same room before we do this.
        /// </summary>
        private bool AttemptWalk()
        {
            if (VMFeatures.DynamicObstacles)
            {
                if (AttemptWalkWith(true)) return true;
                //A Sim standing still made the room impassable to the planner. Plan as before, so the Sim walks up to
                //them and the collision handling waits or asks them to move.
                if (!IncludedStandingAvatars) return false;
            }
            return AttemptWalkWith(false);
        }

        //set by AttemptWalkWith when a standing avatar was added only because of VMFeatures.DynamicObstacles.
        private bool IncludedStandingAvatars;
        //room and object version the current walk was planned with (VMFeatures.DynamicObstacles). Not saved: a loaded
        //route plans again at most once.
        private ushort PlannedRoom;
        private int PlannedObjectVersion;
        //per goal: how many times a failed door was tried again (VMFeatures.DynamicObstacles)
        private int PortalRetries;

        private const int ESCAPE_RADIUS = 24;
        private const int STANDING_GOAL_CLEARANCE = 8;

        private bool AttemptWalkWith(bool standingAvatars)
        {
            //find shortest path to destination tile. Simple A* pathfind.
            //portals are used to traverse floors, so we do not care about the floor each point is on.
            //when evaluating possible adjacent tiles we use the Caller's current floor.

            LotTilePos startPos = Caller.Position;
            //CurrentPath = VMPathLineSegment.LoP(Caller.Position.ToPoint(), CurRoute.Position.ToPoint());
            //CurrentPath.CalculateTotalFrames();
            //CurrentWaypoint = CurRoute.Position;
            WalkTo = null;
            IncludedStandingAvatars = false;

            var startPoint = new Point((int)startPos.x, (int)startPos.y);
            var endPoint = new Point((int)CurRoute.Position.x, (int)CurRoute.Position.y);

            if (startPoint == endPoint)
            {
                State = VMRoutingFrameState.TURN_ONLY;
                return true;
            }

            var myRoom = VM.Context.GetRoomAt(startPos);
            if (myRoom == 0) return false;

            var roomInfo = VM.Context.RoomInfo[myRoom];

            int bx = (roomInfo.Room.Bounds.X-1) << 4;
            int by = (roomInfo.Room.Bounds.Y-1) << 4;
            int width = (roomInfo.Room.Bounds.Width+2) << 4;
            int height = (roomInfo.Room.Bounds.Height+2) << 4;

            var obstacles = new VMObstacleSet(roomInfo.Room.RoutingObstacles);//new List<VMObstacle>();

            obstacles.Add(new VMObstacle(bx-16, by-16, bx+width+16, by));
            obstacles.Add(new VMObstacle(bx-16, by+height, bx+width+16, by+height+16));

            obstacles.Add(new VMObstacle(bx-16, by-16, bx, by+height+16));
            obstacles.Add(new VMObstacle(bx+width, by-16, bx+width+16, by+height+16));

            var considerAvatars = !Caller.GetFlag(VMEntityFlags.AllowPersonIntersection);
            var shimmyCandidates = CanShimmy() ? new List<VMShimmyCandidate>() : null;
            var objectObstacles = new List<KeyValuePair<VMEntity, VMObstacle>>();

            foreach (var obj in roomInfo.Entities)
            {
                var ft = obj.Footprint;

                var flags = (VMEntityFlags)obj.GetValue(VMStackObjectVariable.Flags);
                bool standing = false;
                if (obj != Caller && ft != null &&
                    (obj is VMGameObject || (considerAvatars && (AvatarsToConsider.Contains(obj)
                        || (standing = standingAvatars && IsStandingInTheWay(obj, startPoint, endPoint))))) &&
                    ((flags & VMEntityFlags.DisallowPersonIntersection) > 0 || (flags & VMEntityFlags.AllowPersonIntersection) == 0)
                    && (!(Caller.ExecuteEntryPoint(5, VM.Context, true, obj, new short[] { obj.ObjectID, 1, 0, 0 })
                        || obj.ExecuteEntryPoint(5, VM.Context, true, Caller, new short[] { Caller.ObjectID, 1, 0, 0 }))))
                {
                    var inflated = new VMObstacle(ft.x1 - 3, ft.y1 - 3, ft.x2 + 3, ft.y2 + 3);
                    obstacles.Add(inflated);
                    if (standing) IncludedStandingAvatars = true;
                    if (obj is VMGameObject) objectObstacles.Add(new KeyValuePair<VMEntity, VMObstacle>(obj, inflated));
                    if (shimmyCandidates != null && obj is VMGameObject)
                    {
                        shimmyCandidates.Add(new VMShimmyCandidate()
                        {
                            Footprint = new VMObstacle(ft.x1, ft.y1, ft.x2, ft.y2),
                            Inflated = inflated,
                            Owner = obj,
                            Group = obj.MultitileGroup
                        });
                    }
                }
            }

            int dir = (int)DirectionUtils.PosMod(Math.Round(Caller.RadianDirection / (Math.PI / 2f)), 4);

            VMShimmyPlanner planner = null;
            if (shimmyCandidates != null && shimmyCandidates.Count > 1)
            {
                //with no pinches in the room this falls through to the unchanged legacy route below.
                planner = new VMShimmyPlanner(obstacles, shimmyCandidates);
                if (planner.Pinches.Count == 0) planner = null;
                else if (AttemptShimmyWalk(planner, startPoint, endPoint, dir)) return Planned(myRoom, null);
            }

            VMPathShimmySegment escape = null;
            if (obstacles.SearchForIntersect(new VMObstacle(startPoint, startPoint)))
            {
                //standing inside an obstacle's clearance: no route can start here.
                if (!VMFeatures.Unstick) return false;
                escape = FindEscape(obstacles, objectObstacles, startPoint, endPoint);
                if (escape == null) return false;
                startPoint = new Point(escape.To.X / 0x8000, escape.To.Y / 0x8000);
                if (planner != null)
                {
                    if (!AttemptShimmyWalk(planner, startPoint, endPoint, dir)) return false;
                    return Planned(myRoom, escape);
                }
            }
            else if (planner != null) return false; //the planner already tried every way, including the direct one

            var router = new VMRectRouter(obstacles);

            var rectRoute = router.Route(startPoint, endPoint, dir);// (6-dir)%4);
            var parent = ParentRoute ?? this;
            var inDir = (parent.State == VMRoutingFrameState.ROOM_PORTAL || parent.PortalTurns > 0) ? Caller.RadianDirection : (float?)null;
            router.OptimizeLines(rectRoute, endPoint, inDir);
            var useBezier = (VM.Tuning?.GetTuning("feature", 0, 1) ?? 0) == 0;
            if (useBezier)
            {
                WalkTo = VMPathBezierSegment.GeneratePath(rectRoute, endPoint,
                    inDir,
                    CurRoute.FaceAnywhere ? (float?)null : CurRoute.RadianDirection);
            } else
            {
                WalkTo = VMPathLineSegment.GeneratePath(rectRoute, endPoint);
            }
            if (DEBUG_DRAW) DebugDraw(rectRoute);
            if (WalkTo != null)
            {
                //if (WalkTo.First.Value.Source != endPoint && WalkTo.Count > 1) WalkTo.RemoveFirst();
                AdvanceWaypoint();
                return Planned(myRoom, escape);
            }
            return false;
        }

        /// <summary>
        /// Records what the new walk was planned with, and puts the escape step (if any) in front of it.
        /// </summary>
        private bool Planned(ushort room, VMPathShimmySegment escape)
        {
            if (escape != null && WalkTo != null)
            {
                //the planner already made its first segment current: put it back behind the escape step.
                if (CurrentPath != null) WalkTo.AddFirst(CurrentPath);
                WalkTo.AddFirst(escape);
                CurrentPath = null;
                AdvanceWaypoint();
                VMDiagnostics.Route(Caller, VMRouteEventType.Unstuck, other: VM.GetObjectById(escape.ObjectA),
                    goal: CurRoute?.Position, detail: "stepped out of an object's clearance");
            }
            PlannedRoom = room;
            var versions = VM.Context.RoomObjectVersion;
            PlannedObjectVersion = (room < versions.Length) ? versions[room] : 0;
            return true;
        }

        /// <summary>
        /// True if the room the walk was planned in had an object placed, moved or removed since.
        /// </summary>
        private bool RoomChangedSincePlan()
        {
            var room = VM.Context.GetRoomAt(Caller.Position);
            if (room != PlannedRoom) return false;
            var versions = VM.Context.RoomObjectVersion;
            return room < versions.Length && versions[room] != PlannedObjectVersion;
        }

        /// <summary>
        /// A Sim that is standing still (not walking a route) and is not at our start or our destination. Such Sims are
        /// planned around (VMFeatures.DynamicObstacles). Sims near the destination are left to the existing collision
        /// handling, which waits for them or asks them to move.
        /// </summary>
        private bool IsStandingInTheWay(VMEntity obj, Point start, Point end)
        {
            var avatar = obj as VMAvatar;
            if (avatar == null || avatar.Container != null || avatar.Dead) return false;
            var top = avatar.Thread?.Stack.LastOrDefault() as VMRoutingFrame;
            if (top != null && top.WaitTime == 0 && top.State != VMRoutingFrameState.FAILED) return false; //walking
            var ft = avatar.Footprint;
            var clear = new VMObstacle(ft.x1 - STANDING_GOAL_CLEARANCE, ft.y1 - STANDING_GOAL_CLEARANCE,
                ft.x2 + STANDING_GOAL_CLEARANCE, ft.y2 + STANDING_GOAL_CLEARANCE);
            return !clear.HardContains(start) && !clear.HardContains(end);
        }

        /// <summary>
        /// For a Sim standing inside the clearance of one or two objects: the nearest free point within 1.5 tiles
        /// (preferring the direction of the goal), as a step that may overlap those objects, like a shimmy.
        /// Returns null if more objects, a Sim or a wall trap it, or nothing is free nearby.
        /// </summary>
        private VMPathShimmySegment FindEscape(VMObstacleSet obstacles, List<KeyValuePair<VMEntity, VMObstacle>> objects, Point start, Point end)
        {
            var trapping = objects.Where(x => x.Value.HardContains(start)).ToList();
            if (trapping.Count == 0 || trapping.Count > 2) return null;
            //something else (a Sim, a wall, a third object) also covers the start: leave it to the old behaviour.
            var trappingObs = new HashSet<VMObstacle>(trapping.Select(x => x.Value));
            if (obstacles.AllIntersect(new VMObstacle(start, start)).Any(x => !trappingObs.Contains(x))) return null;

            Point? best = null;
            double bestScore = double.MaxValue;
            for (int r = 2; r <= ESCAPE_RADIUS && best == null; r += 2)
            {
                for (int i = 0; i < 16; i++)
                {
                    var a = i * Math.PI / 8;
                    var p = new Point(start.X + (int)Math.Round(Math.Cos(a) * r), start.Y + (int)Math.Round(Math.Sin(a) * r));
                    if (obstacles.SearchForIntersect(new VMObstacle(p, p))) continue;
                    if (!EscapeClear(obstacles, trappingObs, start, p)) continue;
                    var dx = end.X - p.X;
                    var dy = end.Y - p.Y;
                    var score = Math.Sqrt(dx * dx + dy * dy);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = p;
                    }
                }
            }
            if (best == null) return null;

            var move = VMShimmyPlanner.Heading(start, best.Value);
            bool stepRight = false;
            var facing = ((VMAvatar)Caller).IsPet ? move : VMShimmyPlanner.ChooseFacing(move, Caller.RadianDirection, out stepRight);
            var a1 = trapping[0].Key;
            var a2 = (trapping.Count > 1) ? trapping[1].Key : a1;
            return new VMPathShimmySegment(new Point(start.X * 0x8000, start.Y * 0x8000),
                new Point(best.Value.X * 0x8000, best.Value.Y * 0x8000), a1.ObjectID, a2.ObjectID, facing, stepRight);
        }

        private static bool EscapeClear(VMObstacleSet obstacles, HashSet<VMObstacle> allowed, Point from, Point to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy)));
            for (int i = 1; i <= steps; i++)
            {
                var pt = new Point(from.X + dx * i / steps, from.Y + dy * i / steps);
                if (obstacles.AllIntersect(new VMObstacle(pt, pt)).Any(x => !allowed.Contains(x))) return false;
            }
            return true;
        }

        private bool CanShimmy()
        {
            //swimming is left alone.
            if (!VMShimmyPlanner.Enabled || !VM.TS1) return false;
            var avatar = Caller as VMAvatar;
            if (avatar == null || InPool) return false;
            //CONFIRMED (ExpansionPack5.far, TemplateDog/TemplateCat STR 150): pets have no side-step animation, entries
            //11-18 are "d2o-stand-still" or empty. Pets squeeze through facing forwards with their walk cycle instead.
            if (avatar.IsPet) return true;
            var anims = avatar.WalkAnimations;
            return !string.IsNullOrEmpty(anims[ANIM_ADJUST_LEFT]) && !string.IsNullOrEmpty(anims[ANIM_ADJUST_RIGHT]);
        }

        private int ShimmyVelocity(VMAvatar avatar)
        {
            return avatar.IsPet ? SHIMMY_VELOCITY_PET : SHIMMY_VELOCITY;
        }

        /// <summary>
        /// Builds WalkTo from a route that may contain shimmy legs (see VMShimmyPlanner). Rect legs become line or bezier
        /// segments exactly as in AttemptWalk; shimmy legs become VMPathShimmySegments.
        /// </summary>
        private bool AttemptShimmyWalk(VMShimmyPlanner planner, Point startPoint, Point endPoint, int dir)
        {
            var legs = planner.Plan(startPoint, endPoint, dir);
            if (legs == null) return false;

            var parent = ParentRoute ?? this;
            float? inDir = (parent.State == VMRoutingFrameState.ROOM_PORTAL || parent.PortalTurns > 0) ? Caller.RadianDirection : (float?)null;
            var useBezier = (VM.Tuning?.GetTuning("feature", 0, 1) ?? 0) == 0;
            var path = new LinkedList<VMIPathSegment>();
            var debugRects = new List<VMWalkableRect>();
            float heading = Caller.RadianDirection;

            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (leg is VMRectRouteLeg)
                {
                    var rect = (VMRectRouteLeg)leg;
                    var next = (i + 1 < legs.Count) ? legs[i + 1] : null;
                    //arrive at a mouth already moving across the pinch
                    float? outDir = (next != null) ? VMShimmyPlanner.Heading(next.From, next.To)
                        : (CurRoute.FaceAnywhere ? (float?)null : CurRoute.RadianDirection);

                    new VMRectRouter(null).OptimizeLines(rect.Rects, rect.To, inDir);
                    var segments = useBezier ? VMPathBezierSegment.GeneratePath(rect.Rects, rect.To, inDir, outDir)
                        : VMPathLineSegment.GeneratePath(rect.Rects, rect.To);
                    if (segments == null) return false;
                    foreach (var segment in segments) path.AddLast(segment);
                    debugRects.AddRange(rect.Rects);
                    if (path.Count > 0)
                    {
                        var last = path.Last.Value;
                        if (last.Source != last.Destination)
                            heading = (float)Math.Atan2(last.Destination.X - last.Source.X, last.Source.Y - last.Destination.Y);
                    }
                    inDir = null;
                }
                else
                {
                    var shimmy = (VMShimmyRouteLeg)leg;
                    var move = VMShimmyPlanner.Heading(shimmy.From, shimmy.To);
                    bool stepRight = false;
                    //pets walk through facing forwards, Sims side-step facing the perpendicular nearest their heading.
                    var facing = ((VMAvatar)Caller).IsPet ? move : VMShimmyPlanner.ChooseFacing(move, heading, out stepRight);
                    path.AddLast(new VMPathShimmySegment(
                        new Point(shimmy.From.X * 0x8000, shimmy.From.Y * 0x8000),
                        new Point(shimmy.To.X * 0x8000, shimmy.To.Y * 0x8000),
                        ((VMEntity)shimmy.Pinch.A.Owner).ObjectID,
                        ((VMEntity)shimmy.Pinch.B.Owner).ObjectID,
                        facing, stepRight));
                    heading = facing;
                    inDir = move;
                }
            }

            WalkTo = path;
            if (DEBUG_DRAW) DebugDraw(debugRects, planner);
            AdvanceWaypoint();
            return true;
        }

        private void DebugDraw(IEnumerable<VMWalkableRect> rects, VMShimmyPlanner planner = null)
        {
            if (rects == null || !VM.UseWorld) return;
            var component = new DebugLinesComponent(VM.Context.Blueprint);
            component.Level = Caller.Position.Level;
            component.Owner = this;

            foreach (var rect in rects)
            {
                component.AddRectangle(rect.ToRectangle(), Color.White);
            }

            if (planner != null)
            {
                //pinches found in this room: the two footprints, and the side-step line between the mouths.
                foreach (var pinch in planner.Pinches)
                {
                    component.AddRectangle(pinch.A.Footprint.ToRectangle(), Color.Yellow);
                    component.AddRectangle(pinch.B.Footprint.ToRectangle(), Color.Yellow);
                    component.AddLine(pinch.MouthA.ToVector2() / 16f, pinch.MouthB.ToVector2() / 16f, Color.Yellow);
                }
            }

            var path = new List<Vector2>();
            bool start = true;
            foreach (var segment in WalkTo)
            {
                segment.AddToPath(path, start);
                segment.AddDebugExtras(component);
                start = false;
            }
            component.AddPath(path, Color.Red);

            VM.Context.Blueprint.DebugLines.Add(component);
        }

        public void DebugRemove()
        {
            var toRemove = VM.Context.Blueprint.DebugLines.Where(x => x.Owner == this).ToList();
            foreach (var remove in toRemove)
            {
                remove.Dispose();
                VM.Context.Blueprint.DebugLines.Remove(remove);
            }
        }

        private void OpenSetSortedInsert(List<VMRoomPortal> set, Dictionary<VMRoomPortal, double> fScore, VMRoomPortal portal)
        {
            var myScore = fScore[portal];
            for (int i = 0; i < set.Count; i++)
            {
                if (myScore < fScore[set[i]])
                {
                    set.Insert(i, portal);
                    return;
                }
            }
            set.Add(portal);
        }

        private double GetDist(LotTilePos pos1, LotTilePos pos2)
        {
            return Math.Sqrt(Math.Pow(pos1.x - pos2.x, 2) + Math.Pow(pos1.y - pos2.y, 2))/16.0 + Math.Abs(pos1.Level-pos2.Level)*10;
        }

        private bool PushEntryPoint(int entryPoint, VMEntity ent) {
            if (ent.EntryPoints[entryPoint].ActionFunction != 0)
            {
                bool Execute;
                if (ent.EntryPoints[entryPoint].ConditionFunction != 0) //check if we can definitely execute this...
                {
                    var Behavior = ent.GetRoutineWithOwner(ent.EntryPoints[entryPoint].ConditionFunction, VM.Context);
                    if (Behavior != null)
                    {
                        Execute = (VMThread.EvaluateCheck(VM.Context, Caller, new VMStackFrame()
                        {
                            Caller = Caller,
                            Callee = ent,
                            CodeOwner = Behavior.owner,
                            StackObject = ent,
                            Routine = Behavior.routine,
                            Args = new short[4]
                        }) == VMPrimitiveExitCode.RETURN_TRUE);
                    }
                    else Execute = true;

                }
                else
                {
                    Execute = true;
                }

                if (Execute)
                {
                    //push it onto our stack, except now the object owns our soul! when we are returned to we can evaluate the result and determine if the action failed.
                    var Behavior = ent.GetRoutineWithOwner(ent.EntryPoints[entryPoint].ActionFunction, VM.Context);
                    if (Behavior == null) return false; //invalid id
                    var routine = Behavior.routine;
                    var childFrame = new VMStackFrame
                    {
                        Routine = routine,
                        Caller = Caller,
                        Callee = ent,
                        CodeOwner = Behavior.owner,
                        StackObject = ent,
                        ActionTree = ActionTree
                    };
                    childFrame.Args = new short[routine.Arguments];
                    Thread.Push(childFrame);
                    return true;
                }
                else
                {
                    return false; //could not execute portal function. todo: re-evaluate room route
                }
            }
            else
            {
                return false;
            }
        }

        public VMPrimitiveExitCode Tick()
        {
            VM.Context.NextRandom(1); //rng cycle - for desync detect
            var avatar = (VMAvatar)Caller;

            if (State != VMRoutingFrameState.FAILED && avatar.GetFlag(VMEntityFlags.InteractionCanceled) && avatar.GetPersonData(VMPersonDataVariable.NonInterruptable) == 0)
            {
                HardFail(VMRouteFailCode.Interrupted, null);
                return VMPrimitiveExitCode.CONTINUE;
            }

            if (WaitTime > 0)
            {
                if (Velocity > 0) Velocity--;

                if (avatar.Animations.Count < 3) StartWalkAnimation();
                UpdateWalkWeights(avatar);

                WaitTime--;
                Timeout--;
                if (Timeout <= 0)
                {
                    VMDiagnostics.Route(Caller, VMRouteEventType.Timeout, VMRouteFailCode.NoPath, goal: CurRoute?.Position);
                    //try again. not sure if we should reset timeout for the new route
                    SoftFail(VMRouteFailCode.NoPath, null);
                    if (VMFeatures.RoutingFixes)
                    {
                        //AttemptRoute refilled the allowance and cleared the wait for the new goal. (Previously WaitTime
                        //stayed above 0 with Timeout at 0, so every following tick gave up another goal.)
                        WaitTime = 0;
                        if (State != VMRoutingFrameState.FAILED) Velocity = 0;
                    }
                    else if (State != VMRoutingFrameState.FAILED) {
                        Velocity = 0;
                        State = VMRoutingFrameState.WALKING;
                    }
                } else return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
            }

            if (RoomRouteInvalid && State != VMRoutingFrameState.BEGIN_TURN && State != VMRoutingFrameState.END_TURN && State != VMRoutingFrameState.FAILED && State != VMRoutingFrameState.TURN_ONLY)
            {
                RoomRouteInvalid = false;
                IgnoredRooms.Clear();

                WalkTo = null; //reset routing state
                if (!DoRoomRoute(CurRoute))
                {
                    if (CurRoute != null) SoftFail(VMRouteFailCode.NoRoomRoute, null);
                    else HardFail(VMRouteFailCode.NoRoomRoute, null);
                }
                else if (Rooms.Count > 0)
                {
                    State = VMRoutingFrameState.INITIAL;
                }
            }

            switch (State)
            {
                case VMRoutingFrameState.STAND_FUNC:
                    if (Thread.LastStackExitCode == VMPrimitiveExitCode.RETURN_TRUE)
                    {
                        State = VMRoutingFrameState.INITIAL;
                        if (avatar.GetPersonData(VMPersonDataVariable.Posture) == 1) avatar.SetPersonData(VMPersonDataVariable.Posture, 0);
                    }
                    else {
                        var resultID = avatar.GetValue(VMStackObjectVariable.PrimitiveResultID);
                        SoftFail(VMRouteFailCode.CantStand, (resultID == 0)? null : VM.GetObjectById(resultID));
                    }
                    return VMPrimitiveExitCode.CONTINUE;
                case VMRoutingFrameState.INITIAL:
                case VMRoutingFrameState.ROOM_PORTAL:
                    //check if the room portal that just finished succeeded.
                    if (State == VMRoutingFrameState.ROOM_PORTAL) { 
                        if (Thread.LastStackExitCode != VMPrimitiveExitCode.RETURN_TRUE)
                        {
                            IgnoredRooms.Add(CurrentPortal);
                            State = VMRoutingFrameState.INITIAL;
                            if (!DoRoomRoute(CurRoute))
                            {
                                if (VMFeatures.DynamicObstacles && PortalRetries < 1)
                                {
                                    //no other way round: the door may only have been in use or blocked for a moment.
                                    //wait a little and try it once more before giving up on this goal.
                                    PortalRetries++;
                                    IgnoredRooms.Remove(CurrentPortal);
                                    VMDiagnostics.Route(Caller, VMRouteEventType.DoorRetry, VMRouteFailCode.NoRoomRoute,
                                        VM.GetObjectById(CurrentPortal.ObjectID), CurRoute?.Position);
                                    if (DoRoomRoute(CurRoute))
                                    {
                                        WaitTime = 60;
                                        return VMPrimitiveExitCode.CONTINUE;
                                    }
                                }
                                SoftFail(VMRouteFailCode.NoRoomRoute, null); //todo: reattempt room route with portal we tried removed.
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                        }
                    }

                    if (Rooms.Count > 0)
                    { //push portal function of next portal
                        CurrentPortal = Rooms.Pop();
                        var ent = VM.GetObjectById(CurrentPortal.ObjectID);
                        State = VMRoutingFrameState.ROOM_PORTAL;
                        if (!PushEntryPoint(15, ent)) //15 is portal function
                            SoftFail(VMRouteFailCode.NoRoomRoute, null); //could not execute portal function
                        return VMPrimitiveExitCode.CONTINUE;
                    }

                    //if we're here, room route is OK. start routing to a destination.
                    if (Choices == null)
                    {
                        //perform slot parse.
                        if (Slot == null)
                        {
                            HardFail(VMRouteFailCode.Unknown, null);
                            return VMPrimitiveExitCode.CONTINUE; //this should never happen. If it does, someone has used the routing system incorrectly.
                        }

                        var parser = new VMSlotParser(Slot);

                        Choices = parser.FindAvaliableLocations(Target, VM.Context, avatar);
                        TargetPos = Target.Position;
                        TargetPosKnown = true;
                        if (VMFeatures.Diagnostics)
                            VMDiagnostics.Route(Caller, VMRouteEventType.Choices, parser.FailCode, parser.Blocker, Target.Position,
                                Choices.Count + " position(s); " + parser.DescribeRejections());
                        if (Choices.Count == 0)
                        {
                            HardFail(parser.FailCode, parser.Blocker);
                            return VMPrimitiveExitCode.CONTINUE;
                        }
                        else
                        {
                            CurRoute = Choices[0];
                            Choices.RemoveAt(0);
                        }
                    }

                    //do we need to sit in a seat? it should take over.
                    if (CurRoute.Chair != null)
                    {
                        if (!AttemptedChair)
                        {
                            AttemptedChair = true;
                            if (PushEntryPoint(26, CurRoute.Chair)) return VMPrimitiveExitCode.CONTINUE;
                            else
                            {
                                SoftFail(VMRouteFailCode.CantSit, null);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                        }
                        else
                        {
                            if (Thread.LastStackExitCode == VMPrimitiveExitCode.RETURN_TRUE)
                            {
                                PreExit();
                                return VMPrimitiveExitCode.RETURN_TRUE;
                            }
                            else
                            {
                                SoftFail(VMRouteFailCode.CantSit, null);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                        }
                    }

                    //If we are sitting, and the target is not this seat we need to call the stand function on the object we are contained within.
                    if (avatar.GetPersonData(VMPersonDataVariable.Posture) == 1)
                    {
                        State = VMRoutingFrameState.STAND_FUNC;
                        //push it onto our stack, except now the portal owns our soul! when we are returned to we can evaluate the result and determine if the route failed.
                        var chair = Caller.Container;

                        if (chair == null) avatar.SetPersonData(VMPersonDataVariable.Posture, 0); //we're sitting, but are not bound to a chair. Just instantly get up..
                        else
                        {
                            if (!PushEntryPoint(27, chair)) //27 is stand. TODO: set up an enum for these
                                HardFail(VMRouteFailCode.CantStand, null);
                            return VMPrimitiveExitCode.CONTINUE;
                        }
                    }

                    //no chair, we just need to walk to the spot. Start the within-room routing.
                    if (WalkTo == null)
                    {
                        if (!AttemptWalk())
                        {
                            SoftFail(VMRouteFailCode.NoPath, null);
                            return VMPrimitiveExitCode.CONTINUE;
                        }
                    }
                    if (State != VMRoutingFrameState.TURN_ONLY) BeginWalk();

                    return VMPrimitiveExitCode.CONTINUE;
                case VMRoutingFrameState.FAILED:
                    PreExit();
                    return VMPrimitiveExitCode.RETURN_FALSE;
                case VMRoutingFrameState.TURN_ONLY:
                    if (EndWalk())
                    {
                        PreExit();
                        return VMPrimitiveExitCode.RETURN_TRUE;
                    } else
                    {
                        return VMPrimitiveExitCode.CONTINUE;
                    }
                case VMRoutingFrameState.SHOOED:
                    StartWalkAnimation();
                    State = VMRoutingFrameState.WALKING;
                    return VMPrimitiveExitCode.CONTINUE;
                case VMRoutingFrameState.BEGIN_TURN:
                case VMRoutingFrameState.END_TURN:
                    if (avatar.CurrentAnimationState.EndReached)
                    {
                        if (State == VMRoutingFrameState.BEGIN_TURN)
                        {
                            State = VMRoutingFrameState.WALKING;
                            WalkDirection = TargetDirection;
                            avatar.RadianDirection = (float)TargetDirection;
                            StartWalkAnimation();
                            return VMPrimitiveExitCode.CONTINUE;
                        }
                        else
                        {
                            if (!CurRoute.FaceAnywhere) avatar.RadianDirection = CurRoute.RadianDirection;

                            //reset animation, so that we're facing the correct direction afterwards.
                            avatar.Animations.Clear();

                            var anims = InPool ? avatar.SwimAnimations : avatar.WalkAnimations;

                            var animation = FSO.Content.Content.Get().AvatarAnimations.Get(anims[3] + ".anim");
                            var state = new VMAnimationState(animation, false);
                            state.Loop = true;
                            avatar.Animations.Add(state);

                            PreExit();
                            return VMPrimitiveExitCode.RETURN_TRUE; //we are here!
                        }
                    }
                    else
                    {
                        avatar.TurnVelocity = TurnTweak;
                        avatar.RadianDirection += TurnTweak; //while we're turning, adjust our direction
                        return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                    }
                case VMRoutingFrameState.WALKING:
                    if (WalkTo == null)
                    {
                        if (!AttemptWalk())
                        {
                            SoftFail(VMRouteFailCode.NoPath, null);
                        }
                        return VMPrimitiveExitCode.CONTINUE;
                    }

                    var shimmySegment = CurrentPath as VMPathShimmySegment;
                    if (WalkTo.Count == 0 && MoveTotalFrames - MoveFrames <= 28 && CanPortalTurn()) //7+6+5+4...
                    {
                        //tail off
                        if (Velocity <= 0) Velocity = 1;
                        if (Velocity > 1) Velocity--;
                    }
                    else if (shimmySegment == null && WalkTo.Count > 0 && WalkTo.First.Value is VMPathShimmySegment
                        && MoveTotalFrames - MoveFrames <= 28)
                    {
                        //slow down to side-stepping speed before entering a pinch
                        if (Velocity > ShimmyVelocity(avatar)) Velocity--;
                        else if (Velocity < ShimmyVelocity(avatar)) Velocity++;
                    }
                    else
                    {
                        //get started
                        if (Velocity < 8) Velocity++;
                    }
                    if (shimmySegment != null)
                    {
                        if (Velocity > ShimmyVelocity(avatar)) Velocity = ShimmyVelocity(avatar);
                        //after loading a save the in-place side-step animation must be rebuilt
                        if (!avatar.IsPet && (avatar.Animations.Count != 2 || !VMShimmyAnimation.IsInPlace(avatar.Animations[1].Anim))) StartWalkAnimation();
                    }

                    UpdateWalkWeights(avatar);

                    MoveFrames += Velocity;
                    if (MoveFrames >= MoveTotalFrames)
                    {
                        MoveFrames = MoveTotalFrames;
                        //move us to the final spot, then attempt an advance.
                    }
                    //normal sims can move 0.05 units in a frame.
                    
                    /*
                    if (TurnFrames > 0)
                    {
                        var newDir = (float)(TargetDirection + DirectionUtils.Difference(TargetDirection, WalkDirection) * (TurnFrames / Math.Min(MoveTotalFrames, 10.0)));
                        avatar.TurnVelocity = DirectionUtils.Difference(newDir, avatar.RadianDirection);
                        avatar.RadianDirection = newDir;
                        TurnFrames--;
                    }
                    else
                        avatar.RadianDirection = (float)TargetDirection;
                        */

                    //time to shine
                    var step = CurrentPath.NextPointAndVel(MoveFrames);

                    //var diff = CurrentWaypoint - PreviousPosition;
                    //diff.x = (short)((diff.x * MoveFrames) / MoveTotalFrames);
                    //diff.y = (short)((diff.y * MoveFrames) / MoveTotalFrames);
                    var pos = step.Item1;
                    pos.Level = PreviousPosition.Level;

                    var storedDir = avatar.RadianDirection;
                    VMPlacementResult result;
                    if (shimmySegment != null)
                    {
                        //the avatar box may overlap the two objects it is squeezing between, but nothing else.
                        avatar.ShimmyIgnoreA = VM.GetObjectById(shimmySegment.ObjectA);
                        avatar.ShimmyIgnoreB = VM.GetObjectById(shimmySegment.ObjectB);
                        try
                        {
                            result = Caller.SetPosition(pos, Direction.NORTH, VM.Context);
                        }
                        finally
                        {
                            avatar.ShimmyIgnoreA = null;
                            avatar.ShimmyIgnoreB = null;
                        }
                    }
                    else
                    {
                        result = Caller.SetPosition(pos, Direction.NORTH, VM.Context);
                    }
                    avatar.RadianDirection = storedDir;
                    if (result.Status != VMPlacementError.Success && result.Status != VMPlacementError.CantBeThroughWall)
                    {
                        //route failure, either something changed or we hit an avatar
                        //on both cases try again, but if we hit an avatar then detect if they have a routing frame and stop us for a bit.
                        //we stop the first collider because in most cases they're walking across our walk direction and the problem will go away after a second once they're past.
                        //
                        //if we need to route around a stopped avatar we add them to our "avatars to consider" set.

                        CurrentPath.ResetToFrame(MoveFrames);
                        bool routeAround = true;
                        VMRoutingFrame colRoute = null;
                        VMDiagnostics.Route(Caller, VMRouteEventType.Collision, VMRouteFailCode.Success, result.Object, CurRoute?.Position,
                            result.Status.ToString() + ", " + Retries + " retries left");

                        if (result.Object != null && result.Object is VMAvatar)
                        {
                            var colAvatar = (VMAvatar)result.Object;
                            var colTopFrame = colAvatar.Thread.Stack.LastOrDefault();

                            //we already attempted to move around this avatar... if this happens too much give up.
                            if (AvatarsToConsider.Contains(colAvatar) && --Retries <= 0)
                            {
                                SoftFail(VMRouteFailCode.NoPath, VMFeatures.RoutingFixes ? colAvatar : avatar);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                            bool jobLot = VM.GetGlobalValue(11) > -1;
                            if (Retries <= MAX_RETRIES - 3 && jobLot && !VM.TS1)
                            {
                                Caller.SetFlag(VMEntityFlags.AllowPersonIntersection, true);
                                routeAround = true;
                            }
                            else
                            {
                                if (colTopFrame != null && colTopFrame is VMRoutingFrame)
                                {
                                    colRoute = (VMRoutingFrame)colTopFrame;
                                    routeAround = (colRoute.WaitTime > 0);
                                }
                                else
                                {
                                    --Retries;
                                }
                                if (routeAround) AvatarsToConsider.Add(colAvatar);
                            }
                        }

                        if (result.Object == null || result.Object is VMGameObject)
                        {
                            //this should not happen often. An object or other feature has blocked our path due to some change in its position.
                            //repeated occurances indicate that we are stuck in something.
                            //todo: is this safe for the robot lot?
                            if (--Retries <= 0)
                            {
                                SoftFail(VMRouteFailCode.NoPath, VMFeatures.RoutingFixes ? result.Object : avatar);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                        }

                        if (routeAround)
                        {
                            var oldWalk = new LinkedList<VMIPathSegment>(WalkTo);
                            if (AttemptWalk()) return VMPrimitiveExitCode.CONTINUE;
                            else
                            {
                                //failed to walk around the object
                                if (result.Object is VMAvatar)
                                {
                                    WalkTo = oldWalk;
                                    //if they're a person, shoo them away.
                                    //if they're in a routing frame we can push the tree directly onto their stack
                                    //otherwise we push an interaction and just hope they move (todo: does tso do this?)

                                    //DO NOT push tree if:
                                    // - sim is already being shooed. (the parent of the top routing frame is in state "SHOOED" or shoo interaction is present)
                                    // - we are being shooed.
                                    // - sim is waiting on someone they just shooed. (presumably can move out of our way once they finish waiting)
                                    //instead just wait a small duration and try again later.

                                    //cases where we cannot continue moving increase the retry count. if this is greater than RETRY_COUNT then we fail.
                                    //not sure how the original game works.


                                    if (CanShooAvatar((VMAvatar)result.Object))
                                    {
                                        AvatarsToConsider.Remove((VMAvatar)result.Object);
                                        VMEntity callee = VM.Context.CreateObjectInstance(GOTO_GUID, new LotTilePos(result.Object.Position), Direction.NORTH).Objects[0];
                                        if (colRoute != null)
                                        {
                                            colRoute.State = VMRoutingFrameState.SHOOED;
                                            colRoute.WalkTo = null;
                                            colRoute.AvatarsToConsider.Add(avatar); //just to make sure they don't try route through us.

                                            var tree = callee.GetRoutineWithOwner(SHOO_TREE, VM.Context);
                                            result.Object.Thread.ExecuteSubRoutine(colRoute, tree.routine, tree.owner, new VMSubRoutineOperand());
                                            var frame = result.Object.Thread.Stack.LastOrDefault();
                                            frame.StackObject = callee;
                                            frame.Callee = callee;

                                            WalkInterrupt(60);
                                        }
                                        else
                                        {
                                            callee.PushUserInteraction(SHOO_INTERACTION, result.Object, VM.Context, false);
                                            WalkInterrupt(60);
                                        }
                                    }
                                    else
                                    {
                                        WalkInterrupt(60); //wait for a little while, they're moving out of the way.
                                    }
                                    return VMPrimitiveExitCode.CONTINUE;
                                }
                                SoftFail(VMRouteFailCode.DestTileOccupied, result.Object);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                        }
                        else
                        {
                            WalkInterrupt(30);
                            return VMPrimitiveExitCode.CONTINUE;
                        }

                    }
                    avatar.VisualPosition = new Vector3(step.Item2, (PreviousPosition.Level-1) * 2.95f);//Vector3.Lerp(PreviousPosition.ToVector3(), CurrentWaypoint.ToVector3(), MoveFrames / (float)MoveTotalFrames);
                    avatar.VisualPositionStart = avatar.VisualPosition;

                    avatar.RadianDirection = (float)TargetDirection; //(float)Math.Atan2(avatar.Velocity.X, -avatar.Velocity.Y); //y+ as north. x+ is -90 degrees.

                    var velocity = new Vector3(step.Item3 * Velocity, 0);
                    double diff;
                    if (shimmySegment != null)
                    {
                        //side-stepping: turn towards the shimmy facing, not the movement direction.
                        diff = DirectionUtils.Difference(shimmySegment.Facing, avatar.RadianDirection);
                        if (diff > 0.25) diff = 0.25;
                        else if (diff < -0.25) diff = -0.25;
                    }
                    else
                    {
                        var newTarget = Math.Atan2(velocity.X, -velocity.Y); //y+ as north. x+ is -90 degrees.
                        diff = DirectionUtils.Difference(newTarget, avatar.RadianDirection);
                        var aDiff = Math.Abs(diff);
                        if (aDiff > 0.25)
                        {
                            if (diff > 0) diff = 0.25;
                            else diff = -0.25;
                            if (aDiff > 0.5)
                            {
                                if (Velocity > 4) Velocity -= 2;
                                if (aDiff > 1)
                                {
                                    if (Velocity > 2) Velocity -= 2;
                                    if (aDiff > Math.PI / 2) Velocity = 0;
                                }
                            }
                        }
                    }

                    TargetDirection = DirectionUtils.Normalize(avatar.RadianDirection - diff);
                    avatar.TurnVelocity = diff;
                    //var velocity = Vector3.Lerp(PreviousPosition.ToVector3(), CurrentWaypoint.ToVector3(), Velocity / (float)MoveTotalFrames) - PreviousPosition.ToVector3();
                    velocity.Z = 0;
                    avatar.Velocity = velocity;

                    if (MoveTotalFrames == MoveFrames)
                    {
                        if (VMFeatures.Conversations && shimmySegment == null && TargetMovedAway())
                        {
                            //walking up to a Sim who has since walked off: choose positions around where they are now.
                            Retargets++;
                            VMDiagnostics.Route(Caller, VMRouteEventType.Retarget, other: Target, goal: Target.Position,
                                detail: "target moved away (" + Retargets + " of " + MAX_RETARGETS + ")");
                            Choices = null;
                            var retarget = AttemptRoute(null);
                            if (retarget != VMRouteFailCode.Success)
                            {
                                HardFail(retarget, Target);
                                return VMPrimitiveExitCode.CONTINUE;
                            }
                            Velocity = 0;
                            State = VMRoutingFrameState.INITIAL;
                            return VMPrimitiveExitCode.CONTINUE;
                        }
                        if (VMFeatures.DynamicObstacles && WalkTo.Count > 0 && shimmySegment == null && RoomChangedSincePlan())
                        {
                            //an object was placed, moved or removed in this room since we planned: plan the rest again.
                            var oldWalk = WalkTo;
                            var oldPath = CurrentPath;
                            var oldState = State;
                            if (AttemptWalk())
                            {
                                VMDiagnostics.Route(Caller, VMRouteEventType.Replan, goal: CurRoute?.Position, detail: "room objects changed");
                                if (State == VMRoutingFrameState.TURN_ONLY || WalkTo == null) return VMPrimitiveExitCode.CONTINUE;
                                return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                            }
                            //keep walking the old route; the collision handling deals with whatever is now in the way.
                            WalkTo = oldWalk;
                            CurrentPath = oldPath;
                            State = oldState;
                            PlannedObjectVersion = VM.Context.RoomObjectVersion[PlannedRoom];
                        }
                        MoveTotalFrames = 0;
                        while (MoveTotalFrames == 0)
                        {
                            var remains = AdvanceWaypoint();
                            if (!remains)
                            {
                                MoveTotalFrames = -1;
                                if (EndWalk())
                                {
                                    PreExit();
                                    return VMPrimitiveExitCode.RETURN_TRUE;
                                }
                            }
                        }
                    }
                return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
            }
            return VMPrimitiveExitCode.GOTO_FALSE; //???
        }

        private void PreExit()
        {
            //about to exit the routing frame
            if (State != VMRoutingFrameState.FAILED)
            {
                VMDiagnostics.Route(Caller, VMRouteEventType.Arrived, goal: CurRoute?.Position);
                //a failed route sets RouteResult; without this a later successful route still reported the old failure.
                if (VMFeatures.RoutingFixes && ParentRoute == null) ((VMAvatar)Caller).SetPersonData(VMPersonDataVariable.RouteResult, 0);
            }
            if (DEBUG_DRAW && VM.UseWorld) DebugRemove();
            if (ParentRoute == null)
            {
                var obj = (VMAvatar)Caller;
                obj.SetObstacleStatic(true);
                if (obj.Animations.Count > 1)
                {
                    while (obj.Animations.Count > 1)
                    {
                        obj.Animations.RemoveAt(obj.Animations.Count - 1);
                    }
                    obj.Animations[0].Weight = 1; //return to standing
                }
                if (Caller.PersistID > 0 && Caller.GetFlag(VMEntityFlags.AllowPersonIntersection)
                    && !VM.Context.DisableAvatarCollision)
                {
                    //reset person intersection if we set it
                    Caller.SetFlag(VMEntityFlags.AllowPersonIntersection, false);
                    if (Caller.SetPosition(Caller.Position, Direction.NORTH, VM.Context).Status != VMPlacementError.Success)
                    {
                        //we can't become solid right now
                        Caller.SetFlag(VMEntityFlags.AllowPersonIntersection, true);
                    }
                }
            }
            if (LastWalkStyle != -1) WalkStyle = LastWalkStyle;
        }

        //VMFeatures.Conversations: re-targeting a Sim that walked away. Not saved; after loading, the target's position
        //is taken again at the next waypoint.
        private LotTilePos TargetPos;
        private bool TargetPosKnown;
        private int Retargets;
        private const int MAX_RETARGETS = 3;
        private const int RETARGET_DISTANCE = 24;

        /// <summary>
        /// True if this route walks up to an avatar that has moved more than 1.5 tiles (or changed floors) since its
        /// positions were chosen. (VMFeatures.Conversations)
        /// </summary>
        private bool TargetMovedAway()
        {
            if (Slot == null || ParentRoute != null || Retargets >= MAX_RETARGETS) return false;
            var target = Target as VMAvatar;
            if (target == null || target.Dead || target == Caller || target.Position == LotTilePos.OUT_OF_WORLD) return false;
            if (!TargetPosKnown)
            {
                TargetPos = target.Position;
                TargetPosKnown = true;
                return false;
            }
            if (target.Position.Level != TargetPos.Level) return true;
            var dx = target.Position.x - TargetPos.x;
            var dy = target.Position.y - TargetPos.y;
            return dx * dx + dy * dy > RETARGET_DISTANCE * RETARGET_DISTANCE;
        }

        /// <summary>
        /// True if the avatar is in a social interaction: one of its active actions targets another avatar, or another
        /// avatar's active action targets it. (VMFeatures.Conversations)
        /// </summary>
        private bool IsSocialising(VMAvatar other)
        {
            var thread = other.Thread;
            if (thread == null) return false;
            for (int i = 0; i <= thread.ActiveQueueBlock && i < thread.Queue.Count; i++)
            {
                var callee = thread.Queue[i].Callee;
                if (callee is VMAvatar && callee != other) return true;
            }
            foreach (var ava in VM.Context.ObjectQueries.Avatars)
            {
                if (ava == other || ava.Thread == null) continue;
                var queue = ava.Thread.Queue;
                for (int i = 0; i <= ava.Thread.ActiveQueueBlock && i < queue.Count; i++)
                {
                    if (queue[i].Callee == other) return true;
                }
            }
            return false;
        }

        /// <summary>True if the action this Sim is running was queued by the player (not free will).</summary>
        private bool RunningPlayerAction()
        {
            return Thread.ActiveQueueBlock >= 0 && Thread.Queue.Count > 0 && Thread.Queue[0].Priority > (short)VMQueuePriority.Autonomous;
        }

        private bool CanShooAvatar(VMAvatar avatar)
        {
            //free will walks wait for socialising Sims instead of breaking up their conversation. The player's own
            //commands still shoo, as before.
            if (VMFeatures.Conversations && !RunningPlayerAction() && IsSocialising(avatar)) return false;

            VMRoutingFrame topRoute = null;
            //look for top frame
            for (int i = avatar.Thread.Stack.Count - 1; i >= 0; i--)
            {
                var frame = avatar.Thread.Stack[i];
                if (frame is VMRoutingFrame)
                {
                    topRoute = (VMRoutingFrame)frame;
                    //the comments below mean the top routing frame; without the break this found the bottom one.
                    if (VMFeatures.RoutingFixes) break;
                }
            }

            //check both the top route frame its parent for ones postponed by shooing, 
            //as the top frame will most likely be walking as part of the shoo, and the postponed frame will be the parent.
            //check the parent too just in case they got shooed this frame by another avatar
            if (topRoute != null && (topRoute.State == VMRoutingFrameState.SHOOED || (topRoute.ParentRoute != null && topRoute.ParentRoute.State == VMRoutingFrameState.SHOOED)))
                return false;

            //check if the shoo interaction is present on their action queue. callee GUID (destination obj) and 
            //interaction id must match.
            if (avatar.Thread.Queue.Any(x => x.Callee != null && x.Callee.Object.GUID == GOTO_GUID && x.InteractionNumber == SHOO_INTERACTION)) return false;

            return true;
        }

        public void WalkInterrupt(int waitTime)
        {
            if (State == VMRoutingFrameState.WALKING)
            {
                //only wait if we're walking
                if (WaitTime == 0) VMDiagnostics.Route(Caller, VMRouteEventType.Wait, goal: CurRoute?.Position, detail: waitTime + " ticks");
                WaitTime = Math.Max(waitTime, WaitTime);
            }
        }

        private VMRoutingFrame GetParentFrame()
        {
            //look for parent frame
            for (int i = Thread.Stack.Count - 2; i >= 0; i--)
            {
                var frame = Thread.Stack[i];
                if (frame is VMRoutingFrame)
                {
                    return (VMRoutingFrame)frame;
                    
                }
            }
            return null;
        }

        private bool CanPortalTurn()
        {
            var rf = ParentRoute;
            if (rf == null) rf = this;
            return (rf.State != VMRoutingFrameState.ROOM_PORTAL || rf.PortalTurns++ == 0);
        }

        private void AutoWalkSpeed()
        {
            LastWalkStyle = WalkStyle;
            var dist = ((CurRoute?.Position ?? Target.Position) - Caller.Position).ToVector3().Length();
            if (dist > 10)
            {
                WalkStyle = 1;
            }
        }

        private void BeginWalk()
        { //faces the avatar towards the initial walk direction and begins walking.
            WalkDirection = Caller.RadianDirection;
            var directionDiff = DirectionUtils.Difference(Caller.RadianDirection, TargetDirection);

            bool hardStart = CanPortalTurn();
            Velocity = (hardStart) ? 0 : 8;

            if (hardStart && Turn(directionDiff))
            {
                State = VMRoutingFrameState.BEGIN_TURN;
            }
            else
            {
                State = VMRoutingFrameState.WALKING;
                Caller.RadianDirection = (float)TargetDirection;
                StartWalkAnimation();
            }
        }

        private bool EndWalk() //returns true if we should exit immediately.
        {
            var avatar = (VMAvatar)Caller;
            WalkDirection = Caller.RadianDirection;
            TargetDirection = CurRoute.RadianDirection;

            avatar.SetPersonData(VMPersonDataVariable.RouteEntryFlags, (short)CurRoute.RouteEntryFlags);

            var directionDiff = DirectionUtils.Difference(Caller.RadianDirection, TargetDirection);
            if (!CurRoute.FaceAnywhere && CanPortalTurn() && Turn(directionDiff))
            {
                State = VMRoutingFrameState.END_TURN;
            }
            else
            {
                if (!CurRoute.FaceAnywhere) avatar.RadianDirection = CurRoute.RadianDirection;
                return true; //we are here!
            }
            return false;
        }

        private bool Turn(double directionDiff)
        {
            var obj = (VMAvatar)Caller;
            int off = (directionDiff > 0) ? 0 : 1;
            var absDiff = Math.Abs(directionDiff);
            var anims = (InPool) ? obj.SwimAnimations : obj.WalkAnimations;

            string animName;
            if (absDiff >= Math.PI - 0.01) //full 180 turn
            {
                animName = anims[4 + off];
                TurnTweak = 0;
            }
            else if (absDiff >= (Math.PI / 2) - 0.01) //>=90 degree turn
            {
                animName = anims[6 + off];
                TurnTweak = (float)(absDiff - (Math.PI / 2));
            }
            else if (absDiff >= (Math.PI / 4) - 0.01) //>=45 degree turn
            {
                animName = anims[8 + off];
                TurnTweak = (float)(absDiff - (Math.PI / 4));
            }
            else
            {
                //turning animation not needed for small rotations!
                return false;
            }

            obj.Animations.Clear();
            var anim = PlayAnim(animName, obj);
            if (WalkStyle == 1) anim.Speed = 2f;
            TurnTweak /= anim.Anim.NumFrames/anim.Speed;
            if (off == 1) TurnTweak = -TurnTweak;
            return true;
        }

        private void StartWalkAnimation()
        {
            var obj = (VMAvatar)Caller;
            obj.SetObstacleStatic(false);
            if (CurrentPath is VMPathShimmySegment && !obj.IsPet && StartShimmyAnimation(obj, (VMPathShimmySegment)CurrentPath)) return;
            var pool = VM.Context.RoomInfo[VM.Context.GetRoomAt(Caller.Position)].Room.IsPool;
            var anims = (pool) ? obj.SwimAnimations:obj.WalkAnimations;

            if (obj.Animations.Count == 3 && 
                obj.Animations[0].Anim.Name == anims[3] &&
                obj.Animations[1].Anim.Name == anims[(WalkStyle == 1) ? 21 : 20]) return; //hacky check to test if we're already doing a walking animation. 

            //we set up a very specific collection of animations.
            //The original game gets its walk animation by confusingly combining two of the walk animations and running them at 1.5x speed.
            //We also want to store the standing pose in the first animation slot so that we can blend into and out of it with velocity.

            obj.Animations.Clear();
            var anim = PlayAnim(anims[3], obj); //stand animation (TODO: what about swimming?)
            anim.Weight = 0f;
            anim.Loop = true;
            anim = PlayAnim(anims[(WalkStyle==1)?21:20], obj); //Run full:Walk Full
            anim.Weight = 0.66f;
            anim.Speed = 1.5f;
            anim.Loop = true;
            var hWalkAnim = anims[(WalkStyle == 1 || pool) ? 21 : (obj.IsPet ? 20 : 25)];
            if (hWalkAnim == "") hWalkAnim = anims[(WalkStyle == 1) ? 21 : 20];
            anim = PlayAnim(hWalkAnim, obj); //Run full:Walk Half
            anim.Weight = 0.33f;
            anim.Speed = 1.5f;
            anim.Loop = true;
        }

        /// <summary>
        /// Side-stepping uses [standing loop, in-place standing-adjust-e/w loop], blended by velocity like walking.
        /// Returns false if the avatar has no usable side-step animation (it then keeps its walk animations).
        /// </summary>
        private bool StartShimmyAnimation(VMAvatar obj, VMPathShimmySegment segment)
        {
            var anims = obj.WalkAnimations;
            var name = anims[segment.StepRight ? ANIM_ADJUST_RIGHT : ANIM_ADJUST_LEFT];
            if (string.IsNullOrEmpty(name)) return false;
            var shimmy = VMShimmyAnimation.Get(FSO.Content.Content.Get().AvatarAnimations.Get(name + ".anim"));
            if (shimmy == null) return false;
            if (obj.Animations.Count == 2 && obj.Animations[1].Anim == shimmy.Animation) return true;

            obj.Animations.Clear();
            var stand = PlayAnim(anims[3], obj);
            stand.Weight = 0f;
            stand.Loop = true;
            var step = new VMAnimationState(shimmy.Animation, false);
            step.Loop = true;
            step.Weight = 1f;
            //match the feet to the movement: one loop covers StepDistance at SHIMMY_VELOCITY/10 units per tick.
            step.Speed = (SHIMMY_VELOCITY / 10f) * shimmy.Animation.NumFrames / shimmy.StepDistance;
            obj.Animations.Add(step);
            return true;
        }

        private void UpdateWalkWeights(VMAvatar avatar)
        {
            if (CurrentPath is VMPathShimmySegment && avatar.Animations.Count == 2)
            {
                var weight = Math.Min(1f, Velocity / (float)SHIMMY_VELOCITY);
                avatar.Animations[0].Weight = 1 - weight;
                avatar.Animations[1].Weight = weight;
                return;
            }
            avatar.Animations[0].Weight = (8 - Velocity) / 8f;
            avatar.Animations[1].Weight = (Velocity / 8f) * 0.66f;
            avatar.Animations[2].Weight = (Velocity / 8f) * 0.33f;
        }

        private VMAnimationState PlayAnim(string name, VMAvatar avatar)
        {
            var animation = FSO.Content.Content.Get().AvatarAnimations.Get(name + ".anim");
            var state = new VMAnimationState(animation, false);
            avatar.Animations.Add(state);
            return state;
        }

        private bool AdvanceWaypoint()
        {
            if (WalkTo.Count == 0) return false;

            var point = WalkTo.First.Value;
            WalkTo.RemoveFirst();
            var wasShimmy = CurrentPath is VMPathShimmySegment;
            CurrentPath = point;
            var shimmy = CurrentPath as VMPathShimmySegment;
            /*
            if (WalkTo.Count > 0)
            {
                CurrentPath = point;
                //CurrentWaypoint = new LotTilePos((short)point.X, (short)point.Y, Caller.Position.Level);
            }
            else CurrentPath = new VMPathLineSegment(Caller.Position.ToPoint(), CurRoute.Position.ToPoint()); //go directly to position at last
            // not particularly sure about the above - seems like this failsafe causes impossible routes to be ran through
            */
            PreviousPosition = Caller.Position;
            MoveFrames = 0;

            MoveTotalFrames = CurrentPath.CalculateTotalFrames(); //((LotTilePos.Distance(CurrentWaypoint, Caller.Position) * 20) / 2);
            var avatar = (VMAvatar)Caller;
            if ((avatar.IsPet && avatar.GetPersonData(VMPersonDataVariable.PersonType) < 254) || InPool) MoveTotalFrames *= 2;
            //running does not speed up side-stepping
            MoveTotalFrames = Math.Max(1, MoveTotalFrames/((WalkStyle == 1 && shimmy == null) ? 3 : 1));
            CurrentPath.UpdateTotalFrames(MoveTotalFrames);

            WalkDirection = Caller.RadianDirection;
            if (State == VMRoutingFrameState.WALKING) TargetDirection = WalkDirection;
            else if (shimmy != null) TargetDirection = shimmy.Facing; //turn sideways before the first step
            else TargetDirection = Math.Atan2(CurrentPath.Destination.X - Caller.Position.x * 0x8000, Caller.Position.y * 0x8000 - CurrentPath.Destination.Y); //y+ as north. x+ is -90 degrees.
            TurnFrames = Math.Min(10, MoveTotalFrames);
            if (State == VMRoutingFrameState.WALKING && (wasShimmy || shimmy != null)) StartWalkAnimation();
            return true;
        }

        #region VM Marshalling Functions
        public override VMStackFrameMarshal Save()
        {
            var start = base.Save();

            var aliveAvatars = AvatarsToConsider.Where(x => !x.Dead).ToList();

            var atC = new short[aliveAvatars.Count];
            int i = 0;
            foreach (var item in aliveAvatars)
            {
                atC[i++] = item.ObjectID;
            }

            VMFindLocationResultMarshal[] choices = null;

            if (Choices != null)
            {
                choices = new VMFindLocationResultMarshal[Choices.Count];
                i = 0;
                foreach (var item in Choices)
                {
                    choices[i++] = item.Save();
                }
            }

            return new VMRoutingFrameMarshal
            {
                RoutineID = start.RoutineID,
                InstructionPointer = start.InstructionPointer,
                Caller = start.Caller,
                Callee = start.Callee,
                StackObject = start.StackObject,
                CodeOwnerGUID = start.CodeOwnerGUID,
                Locals = start.Locals,
                Args = start.Args,
                ActionTree = start.ActionTree,
                //above is stack frame stuff

                Rooms = Rooms.ToArray(),
                CurrentPortal = CurrentPortal,

                WalkTo = (WalkTo == null) ? null : WalkTo.ToArray(),
                WalkDirection = WalkDirection,
                TargetDirection = TargetDirection,
                IgnoreRooms = IgnoreRooms,

                State = State,
                PortalTurns = PortalTurns,
                WaitTime = WaitTime,
                Timeout = Timeout,
                Retries = Retries,

                AttemptedChair = AttemptedChair,
                TurnTweak = TurnTweak,
                TurnFrames = TurnFrames,

                MoveTotalFrames = MoveTotalFrames,
                MoveFrames = MoveFrames,
                Velocity = Velocity,

                CallFailureTrees = CallFailureTrees,

                IgnoredRooms = IgnoredRooms.ToArray(),
                AvatarsToConsider = atC,

                PreviousPosition = PreviousPosition,
                CurrentPath = CurrentPath,

                RoomRouteInvalid = RoomRouteInvalid,
                Slot = Slot, //NULLable
                Target = (Target == null) ? (short)0 : Target.ObjectID, //object id
                Choices = choices, //NULLable
                CurRoute = (CurRoute == null) ? null : CurRoute.Save(), //NULLable
                LastWalkStyle = LastWalkStyle
            };
        }

        public override void Load(VMStackFrameMarshal input, VMContext context)
        {
            base.Load(input, context);
            var inR = (VMRoutingFrameMarshal)input;

            Rooms = new Stack<VMRoomPortal>();
            for (int i=inR.Rooms.Length-1; i>=0; i--) Rooms.Push(inR.Rooms[i]);
            CurrentPortal = inR.CurrentPortal;

            ParentRoute = GetParentFrame(); //should be able to, since all arrays are generated left to right, including the stacks.

            WalkTo = (inR.WalkTo == null)?null:new LinkedList<VMIPathSegment>(inR.WalkTo);
            WalkDirection = inR.WalkDirection;
            TargetDirection = inR.TargetDirection;
            IgnoreRooms = inR.IgnoreRooms;

            State = inR.State;
            PortalTurns = inR.PortalTurns;
            WaitTime = inR.WaitTime;
            Timeout = inR.Timeout;
            Retries = inR.Retries;

            AttemptedChair = inR.AttemptedChair;
            TurnTweak = inR.TurnTweak;
            TurnFrames = inR.TurnFrames;

            MoveTotalFrames = inR.MoveTotalFrames;
            MoveFrames = inR.MoveFrames;
            Velocity = inR.Velocity;

            CallFailureTrees = inR.CallFailureTrees;

            IgnoredRooms = new HashSet<VMRoomPortal>(inR.IgnoredRooms);
            AvatarsToConsider = new HashSet<VMAvatar>();

            //these can be dead
            foreach (var avatar in inR.AvatarsToConsider)
                AvatarsToConsider.Add((VMAvatar)context.VM.GetObjectById(avatar));

            PreviousPosition = inR.PreviousPosition;
            CurrentPath = inR.CurrentPath;

            RoomRouteInvalid = inR.RoomRouteInvalid;
            Slot = inR.Slot; //NULLable
            Target = context.VM.GetObjectById(inR.Target); //object id
            if (inR.Choices != null)
            {
                Choices = new List<VMFindLocationResult>();
                foreach (var c in inR.Choices) Choices.Add(new VMFindLocationResult(c, context));
            }
            else Choices = null;
            CurRoute = (inR.CurRoute == null)?null:new VMFindLocationResult(inR.CurRoute, context); //NULLable

            LastWalkStyle = inR.LastWalkStyle;
        }

        public VMRoutingFrame(VMStackFrameMarshal input, VMContext context, VMThread thread)
        {
            Thread = thread;
            Load(input, context);
        }
        #endregion

    }

    public enum VMRoutingFrameState : byte
    {
        INITIAL,
    
        BEGIN_TURN,
        WALKING,
        TURN_ONLY,
        END_TURN,

        SHOOED, //recalculate route once the stack gets back here.
        ROOM_PORTAL,
        STAND_FUNC,
        FAILED
    }
}
