using FSO.Common.Utils;
using FSO.SimAntics.Model.Routing;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Engine.Routing
{
    /// <summary>
    /// An object footprint that a Sim may shimmy past. Footprint is the raw object footprint (what the executor
    /// tests the 6x6 avatar box against); Inflated is the planner obstacle (footprint grown by the avatar half-size),
    /// and must be the same instance that was added to the obstacle set, since obstacles are matched by reference.
    /// </summary>
    public class VMShimmyCandidate
    {
        public VMObstacle Footprint;
        public VMObstacle Inflated;
        public object Owner;
        /// <summary>Multitile group (or any other key). Candidates with the same non-null group never form a pinch
        /// with each other and are ignored together while shimmying.</summary>
        public object Group;
    }

    /// <summary>
    /// A passage between two object footprints that is too narrow for the rectangle router (inflated footprints touch
    /// or overlap), but wide enough for a Sim to side-step through. The passage is crossed with a single straight
    /// segment between the two mouths.
    /// </summary>
    public class VMShimmyPinch
    {
        public VMShimmyCandidate A;
        public VMShimmyCandidate B;
        public Point MouthA;
        public Point MouthB;
        /// <summary>Narrowest gap between the two raw footprints, in 1/16 tile.</summary>
        public float Width;

        public int Index;
    }

    public abstract class VMRouteLeg
    {
        public Point From;
        public Point To;
    }

    public class VMRectRouteLeg : VMRouteLeg
    {
        public LinkedList<VMWalkableRect> Rects;
    }

    public class VMShimmyRouteLeg : VMRouteLeg
    {
        public VMShimmyPinch Pinch;
    }

    /// <summary>
    /// Extends within-room routing with shimmy legs: short sideways crossings of a pinch between two objects.
    ///
    /// Background (Simitone project docs, decisions/0001-shimmy): the planner inflates every object footprint by the avatar
    /// half-size (3) and the rectangle router cannot pass through zero-width gaps. The executor only tests the
    /// 6x6 avatar box against raw footprints. Two objects whose inflated footprints touch or overlap therefore block
    /// routing even when a Sim could physically squeeze between them.
    ///
    /// The planner is pure geometry. With no pinches it returns the legacy rect route unchanged.
    /// </summary>
    public class VMShimmyPlanner
    {
        public static bool Enabled = true;

        //avatar half-size used to inflate footprints for planning (see VMRoutingFrame.AttemptWalk)
        public const int AVATAR_HALF = 3;
        //mouths sit this far outside the raw footprints (inflation + 1 unit of clearance)
        public const int MOUTH_OFFSET = AVATAR_HALF + 1;
        //narrowest raw gap a Sim may side-step through.
        public const float MIN_PINCH_WIDTH = 3;
        //longest overlap two side-by-side objects may have for the gap between them to count as a pinch.
        //longer corridors are not shimmied through (it would look like a Sim crab-walking along a counter).
        public const int MAX_STRAIGHT_LENGTH = 16;
        //route costs are distances, so a shimmy is costed as the walking distance covered in the same time:
        //side-stepping moves at 1/4 of full walking speed (velocity 2 vs 8), and slowing down and turning
        //sideways and back costs roughly another 16 units. Sims therefore still walk around a pinch when the
        //detour is modest, and only shimmy when it is the only way or saves a long walk.
        public const int SHIMMY_COST_MULT = 4;
        public const int SHIMMY_COST_ADD = 16;
        //bounds the work for unreachable destinations, where the search would otherwise try every pair of mouths.
        public const int MAX_PINCHES = 8;
        public const int MAX_ROUTES_PER_PLAN = 64;

        private VMObstacleSet Obstacles;
        private List<VMShimmyCandidate> Candidates;
        private Dictionary<VMObstacle, VMShimmyCandidate> ByInflated;
        public List<VMShimmyPinch> Pinches;

        /// <summary>Number of rect routes evaluated by the last Plan call (instrumentation).</summary>
        public int RoutesEvaluated;

        public VMShimmyPlanner(VMObstacleSet obstacles, List<VMShimmyCandidate> candidates)
        {
            Obstacles = obstacles;
            Candidates = candidates;
            ByInflated = new Dictionary<VMObstacle, VMShimmyCandidate>(ReferenceEqualityComparer.Instance);
            foreach (var c in candidates) ByInflated[c.Inflated] = c;
            Pinches = FindPinches();
        }

        #region Pinch detection

        private List<VMShimmyPinch> FindPinches()
        {
            var result = new List<VMShimmyPinch>();
            for (int i = 0; i < Candidates.Count; i++)
            {
                for (int j = i + 1; j < Candidates.Count; j++)
                {
                    var a = Candidates[i];
                    var b = Candidates[j];
                    if (a.Group != null && a.Group == b.Group) continue;
                    var pinch = TryPinch(a, b);
                    if (pinch != null)
                    {
                        pinch.Index = result.Count;
                        result.Add(pinch);
                    }
                }
            }
            return result;
        }

        private VMShimmyPinch TryPinch(VMShimmyCandidate a, VMShimmyCandidate b)
        {
            var fa = a.Footprint;
            var fb = b.Footprint;
            //separation per axis. positive = gap, negative = overlap.
            var sx = Math.Max(fb.x1 - fa.x2, fa.x1 - fb.x2);
            var sy = Math.Max(fb.y1 - fa.y2, fa.y1 - fb.y2);
            var limit = AVATAR_HALF * 2;
            //inflated footprints do not touch: the rect router can already pass between them.
            if (sx > limit || sy > limit) return null;
            //raw footprints overlap: nothing to pass through.
            if (sx < 0 && sy < 0) return null;

            if (sx >= 0 && sy >= 0) return TryDiagonal(a, b, sx, sy);
            if (sx >= 0) return TryStraight(a, b, false, sx, -sy);
            return TryStraight(a, b, true, sy, -sx);
        }

        /// <summary>
        /// Objects that are diagonal to each other. The passage runs between the two nearest corners, perpendicular
        /// to the line joining them. Each mouth sits 4 units outside both footprints on its side of the passage.
        /// </summary>
        private VMShimmyPinch TryDiagonal(VMShimmyCandidate a, VMShimmyCandidate b, int sx, int sy)
        {
            var width = (float)Math.Sqrt(sx * sx + sy * sy);
            if (width < MIN_PINCH_WIDTH) return null;
            //L is the leftmost footprint, R the rightmost.
            var l = (a.Footprint.x2 <= b.Footprint.x1) ? a : b;
            var r = (l == a) ? b : a;
            var L = l.Footprint;
            var R = r.Footprint;
            Point m1, m2;
            if (R.y2 <= L.y1)
            {
                //R is up-right of L. Corners: L top-right, R bottom-left.
                m1 = new Point(R.x1 - MOUTH_OFFSET, L.y1 - MOUTH_OFFSET);
                m2 = new Point(L.x2 + MOUTH_OFFSET, R.y2 + MOUTH_OFFSET);
            }
            else
            {
                //R is down-right of L. Corners: L bottom-right, R top-left.
                m1 = new Point(L.x2 + MOUTH_OFFSET, R.y1 - MOUTH_OFFSET);
                m2 = new Point(R.x1 - MOUTH_OFFSET, L.y2 + MOUTH_OFFSET);
            }
            return Validate(a, b, m1, m2, width);
        }

        /// <summary>
        /// Objects side by side with a gap between them (overlapping in the other axis). The passage runs along the
        /// gap. If one object extends past the other, the mouth on that end hugs the longer object.
        /// </summary>
        private VMShimmyPinch TryStraight(VMShimmyCandidate a, VMShimmyCandidate b, bool transpose, int gap, int overlap)
        {
            if (gap < MIN_PINCH_WIDTH || overlap > MAX_STRAIGHT_LENGTH) return null;
            var fa = transpose ? Transpose(a.Footprint) : a.Footprint;
            var fb = transpose ? Transpose(b.Footprint) : b.Footprint;
            //gap is along x (after transposing), corridor runs along y.
            var L = (fa.x2 <= fb.x1) ? fa : fb;
            var R = (L == fa) ? fb : fa;
            var lo = Math.Max(L.y1, R.y1);
            var hi = Math.Min(L.y2, R.y2);
            var xs = new int[] { (L.x2 + R.x1) / 2, L.x2 + MOUTH_OFFSET, R.x1 - MOUTH_OFFSET };

            Point? m1 = null, m2 = null;
            foreach (var x in xs)
            {
                var pt = new Point(x, lo - MOUTH_OFFSET);
                if (transpose) pt = new Point(pt.Y, pt.X);
                if (PointFree(pt)) { m1 = pt; break; }
            }
            foreach (var x in xs)
            {
                var pt = new Point(x, hi + MOUTH_OFFSET);
                if (transpose) pt = new Point(pt.Y, pt.X);
                if (PointFree(pt)) { m2 = pt; break; }
            }
            if (m1 == null || m2 == null) return null;
            return Validate(a, b, m1.Value, m2.Value, gap);
        }

        private static VMObstacle Transpose(VMObstacle o)
        {
            return new VMObstacle(o.y1, o.x1, o.y2, o.x2);
        }

        private VMShimmyPinch Validate(VMShimmyCandidate a, VMShimmyCandidate b, Point m1, Point m2, float width)
        {
            if (!PointFree(m1) || !PointFree(m2)) return null;
            if (!SegmentClear(m1, m2, a, b)) return null;
            return new VMShimmyPinch() { A = a, B = b, MouthA = m1, MouthB = m2, Width = width };
        }

        private bool PointFree(Point pt)
        {
            //strict test: a point on an obstacle edge is free (matches VMObstacleSetNode.Intersects).
            return !Obstacles.SearchForIntersect(new VMObstacle(pt, pt));
        }

        private bool InGroups(VMShimmyCandidate c, VMShimmyCandidate a, VMShimmyCandidate b)
        {
            if (c == a || c == b) return true;
            return c.Group != null && (c.Group == a.Group || c.Group == b.Group);
        }

        /// <summary>
        /// True if a Sim can move along from-to while ignoring the inflated footprints of a and b (and their
        /// multitile groups). The raw footprints of those objects must still not be entered, and every other
        /// obstacle (walls, other objects, considered avatars, room bounds) blocks as usual.
        /// </summary>
        private bool SegmentClear(Point from, Point to, VMShimmyCandidate a, VMShimmyCandidate b)
        {
            var bounds = new VMObstacle(Math.Min(from.X, to.X) - 1, Math.Min(from.Y, to.Y) - 1,
                Math.Max(from.X, to.X) + 1, Math.Max(from.Y, to.Y) + 1);
            var blocking = new List<VMObstacle>();
            foreach (var obs in Obstacles.AllIntersect(bounds))
            {
                if (ByInflated.TryGetValue(obs, out var cand) && InGroups(cand, a, b))
                    blocking.Add(cand.Footprint);
                else
                    blocking.Add(obs);
            }
            if (blocking.Count == 0) return true;

            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var steps = Math.Max(1, (int)Math.Ceiling(len * 4));
            for (int i = 0; i <= steps; i++)
            {
                var t = i / (double)steps;
                var px = from.X + dx * t;
                var py = from.Y + dy * t;
                foreach (var o in blocking)
                {
                    if (px > o.x1 && px < o.x2 && py > o.y1 && py < o.y2) return false;
                }
            }
            return true;
        }

        #endregion

        #region Planning

        private class Node
        {
            public Point Pt;
            public VMShimmyPinch Pinch; //null for start and end
            public Node Twin;
            public int Index;
        }

        private class OpenEntry
        {
            public Node Node;
            public Node From;
            public VMRouteLeg Leg; //null until verified (lazy edge evaluation)
            public int G;
            public int F;
            public OpenEntry Parent;
            public int Order;
        }

        private static int Dist(Point a, Point b)
        {
            var dx = (long)(a.X - b.X);
            var dy = (long)(a.Y - b.Y);
            return (int)Math.Sqrt(dx * dx + dy * dy);
        }

        public static int ShimmyCost(Point a, Point b)
        {
            return Dist(a, b) * SHIMMY_COST_MULT + SHIMMY_COST_ADD;
        }

        private static int LegCost(VMRectRouteLeg leg)
        {
            var total = 0;
            var last = leg.From;
            foreach (var rect in leg.Rects)
            {
                total += Dist(last, rect.ParentSource);
                last = rect.ParentSource;
            }
            return total + Dist(last, leg.To);
        }

        private Dictionary<(int, int), VMRectRouteLeg> LegCache;
        private int StartCardinal;

        private VMRectRouteLeg RectLeg(Node from, Node to)
        {
            var key = (from.Index, to.Index);
            if (LegCache.TryGetValue(key, out var cached)) return cached;
            //VMRectRouter adds walkable rects to the set it routes over, so every route needs a fresh copy.
            var router = new VMRectRouter(new VMObstacleSet(Obstacles));
            RoutesEvaluated++;
            var rects = router.Route(from.Pt, to.Pt, (from.Index == 0) ? StartCardinal : 0);
            var leg = (rects == null) ? null : new VMRectRouteLeg() { From = from.Pt, To = to.Pt, Rects = rects };
            LegCache[key] = leg;
            return leg;
        }

        /// <summary>
        /// Plans a route from start to end. Returns null if there is none. Without pinches this is exactly the
        /// legacy single rect route. The start point may lie inside the inflated footprints of a pinch's objects
        /// (e.g. after an interrupted shimmy): it then leaves through one of that pinch's mouths.
        /// </summary>
        public List<VMRouteLeg> Plan(Point start, Point end, int startCardinal)
        {
            StartCardinal = startCardinal;
            RoutesEvaluated = 0;
            LegCache = new Dictionary<(int, int), VMRectRouteLeg>();

            var startNode = new Node() { Pt = start, Index = 0 };
            var endNode = new Node() { Pt = end, Index = 1 };

            var startBlocked = Obstacles.SearchForIntersect(new VMObstacle(start, start));
            VMRectRouteLeg direct = null;
            if (!startBlocked)
            {
                direct = RectLeg(startNode, endNode);
                if (Pinches.Count == 0) return (direct == null) ? null : new List<VMRouteLeg>() { direct };
            }
            //a destination inside an obstacle cannot be reached through a pinch either.
            if (Obstacles.SearchForIntersect(new VMObstacle(end, end))) return (direct == null) ? null : new List<VMRouteLeg>() { direct };

            //pinches the start point can shimmy out of.
            var escapes = new HashSet<VMShimmyPinch>();
            if (startBlocked)
            {
                foreach (var p in Pinches)
                {
                    if (StartEscapes(start, p)) escapes.Add(p);
                }
                if (escapes.Count == 0) return null;
            }

            //keep only pinches that could beat the direct route, nearest (by lower bound) first.
            var directCost = (direct == null) ? int.MaxValue : LegCost(direct);
            var useful = new List<(int bound, VMShimmyPinch pinch)>();
            foreach (var p in Pinches)
            {
                var shimmy = ShimmyCost(p.MouthA, p.MouthB);
                var bound = Math.Min(Dist(start, p.MouthA) + Dist(p.MouthB, end), Dist(start, p.MouthB) + Dist(p.MouthA, end)) + shimmy;
                if (escapes.Contains(p)) bound = 0;
                if (bound < directCost) useful.Add((bound, p));
            }
            if (useful.Count == 0) return (direct == null) ? null : new List<VMRouteLeg>() { direct };
            useful.Sort((x, y) => (x.bound != y.bound) ? x.bound.CompareTo(y.bound) : x.pinch.Index.CompareTo(y.pinch.Index));
            if (useful.Count > MAX_PINCHES) useful.RemoveRange(MAX_PINCHES, useful.Count - MAX_PINCHES);

            var nodes = new List<Node>() { startNode, endNode };
            foreach (var u in useful)
            {
                var n1 = new Node() { Pt = u.pinch.MouthA, Pinch = u.pinch, Index = nodes.Count };
                nodes.Add(n1);
                var n2 = new Node() { Pt = u.pinch.MouthB, Pinch = u.pinch, Index = nodes.Count };
                nodes.Add(n2);
                n1.Twin = n2;
                n2.Twin = n1;
            }

            var result = Search(nodes, startNode, endNode, startBlocked, escapes);
            if (result == null && direct != null) return new List<VMRouteLeg>() { direct }; //should not happen
            return result;
        }

        private bool StartEscapes(Point start, VMShimmyPinch p)
        {
            //every obstacle containing the start must belong to the pinch's objects, without entering their raw footprints.
            foreach (var obs in Obstacles.AllIntersect(new VMObstacle(start, start)))
            {
                if (!ByInflated.TryGetValue(obs, out var cand) || !InGroups(cand, p.A, p.B)) return false;
                if (cand.Footprint.HardContains(start)) return false;
            }
            return SegmentClear(start, p.MouthA, p.A, p.B) || SegmentClear(start, p.MouthB, p.A, p.B);
        }

        private List<VMRouteLeg> Search(List<Node> nodes, Node start, Node end, bool startBlocked, HashSet<VMShimmyPinch> escapes)
        {
            var open = new List<OpenEntry>();
            var closed = new bool[nodes.Count];
            var order = 0;

            Action<OpenEntry> push = (e) =>
            {
                e.Order = order++;
                //sorted insert, stable for equal F (deterministic across clients).
                int i = 0;
                while (i < open.Count && open[i].F <= e.F) i++;
                open.Insert(i, e);
            };

            push(new OpenEntry() { Node = start, G = 0, F = Dist(start.Pt, end.Pt), Leg = new VMRectRouteLeg() });

            while (open.Count > 0)
            {
                var cur = open[0];
                open.RemoveAt(0);
                if (closed[cur.Node.Index]) continue;

                if (cur.Leg == null)
                {
                    if (RoutesEvaluated >= MAX_ROUTES_PER_PLAN && !LegCache.ContainsKey((cur.From.Index, cur.Node.Index))) return null;
                    //lazily evaluate the rect route for this edge, then requeue with its real cost.
                    var leg = RectLeg(cur.From, cur.Node);
                    if (leg == null) continue;
                    cur.Leg = leg;
                    cur.G = cur.Parent.G + LegCost(leg);
                    cur.F = cur.G + Dist(cur.Node.Pt, end.Pt);
                    push(cur);
                    continue;
                }

                closed[cur.Node.Index] = true;
                if (cur.Node == end) return Unwind(cur);

                foreach (var next in nodes)
                {
                    if (closed[next.Index] || next == cur.Node) continue;
                    if (cur.Node == start && startBlocked)
                    {
                        //blocked start: only shimmy out of a pinch we are inside.
                        if (next.Pinch == null || !escapes.Contains(next.Pinch)) continue;
                        if (!SegmentClear(start.Pt, next.Pt, next.Pinch.A, next.Pinch.B)) continue;
                        var g = cur.G + ShimmyCost(start.Pt, next.Pt);
                        push(new OpenEntry()
                        {
                            Node = next, From = cur.Node, Parent = cur, G = g, F = g + Dist(next.Pt, end.Pt),
                            Leg = new VMShimmyRouteLeg() { From = start.Pt, To = next.Pt, Pinch = next.Pinch }
                        });
                        continue;
                    }
                    if (cur.Node.Twin == next)
                    {
                        //cross the pinch. Only allowed if we did not just shimmy into this mouth.
                        if (cur.Leg is VMShimmyRouteLeg) continue;
                        var g = cur.G + ShimmyCost(cur.Node.Pt, next.Pt);
                        push(new OpenEntry()
                        {
                            Node = next, From = cur.Node, Parent = cur, G = g, F = g + Dist(next.Pt, end.Pt),
                            Leg = new VMShimmyRouteLeg() { From = cur.Node.Pt, To = next.Pt, Pinch = next.Pinch }
                        });
                        continue;
                    }
                    var est = cur.G + Dist(cur.Node.Pt, next.Pt);
                    push(new OpenEntry() { Node = next, From = cur.Node, Parent = cur, G = est, F = est + Dist(next.Pt, end.Pt) });
                }
            }
            return null;
        }

        private List<VMRouteLeg> Unwind(OpenEntry entry)
        {
            var result = new List<VMRouteLeg>();
            while (entry.Parent != null)
            {
                if (entry.Leg.From != entry.Leg.To) result.Add(entry.Leg);
                entry = entry.Parent;
            }
            result.Reverse();
            return result;
        }

        #endregion

        #region Facing

        /// <summary>
        /// Heading of a movement in SimAntics radians (0 = north/-y, clockwise), as used by VMEntity.RadianDirection.
        /// </summary>
        public static float Heading(Point from, Point to)
        {
            return (float)Math.Atan2(to.X - from.X, from.Y - to.Y);
        }

        /// <summary>
        /// Picks which way a Sim faces while side-stepping from-to: the perpendicular closest to its current heading.
        /// stepRight is true when the Sim moves towards its own right (facing = heading - 90 degrees).
        /// </summary>
        public static float ChooseFacing(float moveHeading, float currentHeading, out bool stepRight)
        {
            var right = (float)DirectionUtils.Normalize(moveHeading - Math.PI / 2);
            var left = (float)DirectionUtils.Normalize(moveHeading + Math.PI / 2);
            stepRight = Math.Abs(DirectionUtils.Difference(currentHeading, right)) <= Math.Abs(DirectionUtils.Difference(currentHeading, left));
            return stepRight ? right : left;
        }

        #endregion
    }
}
