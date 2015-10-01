﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using EloBuddy;
using EloBuddy.SDK;
using EloBuddy.SDK.Menu.Values;
using EloBuddy.SDK.Rendering;
using SharpDX;
using Color = System.Drawing.Color;

namespace EvadePlus
{
    public class EvadePlus
    {
        public int ServerBuffer
        {
            get { return EvadeMenu.MainMenu["serverBuffer"].Cast<Slider>().CurrentValue; }
        }

        public bool EvadeEnabled
        {
            get { return EvadeMenu.ControlsMenu["enableEvade"].Cast<KeyBind>().CurrentValue && !Player.Instance.IsDead; }
        }

        public bool DodgeDangerousOnly
        {
            get { return EvadeMenu.ControlsMenu["dodgeDangerousOnly"].Cast<KeyBind>().CurrentValue; }
        }

        public int ExtraEvadeRange
        {
            get { return EvadeMenu.MainMenu["extraEvadeRange"].Cast<Slider>().CurrentValue; }
        }

        public bool RandomizeExtraEvadeRange
        {
            get { return EvadeMenu.MainMenu["randomizeExtraEvadeRange"].Cast<CheckBox>().CurrentValue; }
        }

        public bool DrawEvadePoint
        {
            get { return EvadeMenu.DrawMenu["drawEvadePoint"].Cast<CheckBox>().CurrentValue; }
        }

        public bool DrawEvadeStatus
        {
            get { return EvadeMenu.DrawMenu["drawEvadeStatus"].Cast<CheckBox>().CurrentValue; }
        }

        public bool DrawDangerPolygon
        {
            get { return EvadeMenu.DrawMenu["drawDangerPolygon"].Cast<CheckBox>().CurrentValue; }
        }

        public SkillshotDetector SkillshotDetector { get; private set; }

        public EvadeSkillshot[] Skillshots { get; private set; }
        public Geometry.Polygon[] Polygons { get; private set; }
        public List<Geometry.Polygon> ClippedPolygons { get; private set; }
        public Vector2 LastIssueOrderPos;

        private readonly Dictionary<EvadeSkillshot, Geometry.Polygon> _skillshotPolygonCache =
            new Dictionary<EvadeSkillshot, Geometry.Polygon>();

        private EvadeResult LastEvadeResult;
        private Text StatusText;
        private Text StatusTextShadow;

        public EvadePlus(SkillshotDetector detector)
        {
            Skillshots = new EvadeSkillshot[] {};
            Polygons = new Geometry.Polygon[] {};
            ClippedPolygons = new List<Geometry.Polygon>();
            StatusText = new Text("EvadePlus Enabled", new Font("Lucida Sans Unicode", 7.6F, FontStyle.Bold)); //Lucida Console, 9
            StatusTextShadow = new Text("EvadePlus Enabled", new Font("Lucida Sans Unicode", 7.6F, FontStyle.Bold));

            SkillshotDetector = detector;
            SkillshotDetector.OnUpdateSkillshots += OnUpdateSkillshots;
            SkillshotDetector.OnSkillshotActivation += OnSkillshotActivation;
            SkillshotDetector.OnSkillshotDetected += OnSkillshotDetected;
            SkillshotDetector.OnSkillshotDeleted += OnSkillshotDeleted;

            Player.OnIssueOrder += PlayerOnIssueOrder;
            Game.OnTick += Ontick;
            Drawing.OnDraw += OnDraw;
        }

        private void OnUpdateSkillshots(EvadeSkillshot skillshot, bool remove, bool isProcessSpell)
        {
            CacheSkillshots();
            DoEvade();
        }

        private void OnSkillshotActivation(EvadeSkillshot skillshot)
        {
            CacheSkillshots();
            DoEvade();
        }

        private void OnSkillshotDetected(EvadeSkillshot skillshot, bool isProcessSpell)
        {
        }

        private void OnSkillshotDeleted(EvadeSkillshot skillshot)
        {
        }

        private void PlayerOnIssueOrder(Obj_AI_Base sender, PlayerIssueOrderEventArgs args)
        {
            if (args.Order == GameObjectOrder.AttackUnit)
            {
                LastIssueOrderPos =
                    (Player.Instance.Distance(args.Target, true) >=
                     Player.Instance.GetAutoAttackRange(args.Target as AttackableUnit).Pow()
                        ? args.Target.Position
                        : Player.Instance.Position).To2D();
            }
            else
            {
                LastIssueOrderPos = (args.Target != null ? args.Target.Position : args.TargetPosition).To2D();
            }

            CacheSkillshots();
            switch (args.Order)
            {
                case GameObjectOrder.Stop:
                    if (DoEvade())
                    {
                        args.Process = false;
                    }
                    break;

                case GameObjectOrder.HoldPosition:
                    if (DoEvade())
                    {
                        args.Process = false;
                    }
                    break;

                case GameObjectOrder.AttackUnit:
                    if (DoEvade())
                    {
                        args.Process = false;
                    }
                    break;

                default:
                    if (DoEvade(null, Player.Instance.GetPath(LastIssueOrderPos.To3DWorld(), true)))
                    {
                        args.Process = false;
                    }
                    break;
            }
        }

        private void Ontick(EventArgs args)
        {
        }

        private void OnDraw(EventArgs args)
        {
            if (DrawEvadePoint && LastEvadeResult != null)
            {
                if (LastEvadeResult.IsValid && LastEvadeResult.EnoughTime && !LastEvadeResult.Expired())
                {
                    Circle.Draw(new ColorBGRA(255, 0, 0, 255), Player.Instance.BoundingRadius, 25,
                        LastEvadeResult.EvadePoint.To3DWorld());
                }
            }

            if (DrawEvadeStatus)
            {
                StatusText.Color = EvadeEnabled ? Color.White : Color.Red;
                StatusText.TextValue = "EvadePlus " + (EvadeEnabled ? "Enabled" : "Disabled");
                StatusText.Position = Player.Instance.Position.WorldToScreen() -
                                      new Vector2(StatusText.Bounding.Width/2, -20);

                StatusTextShadow.Color = Color.Black;
                StatusTextShadow.TextValue = StatusText.TextValue;
                StatusTextShadow.Position = StatusText.Position - new Vector2(1, 1);

                StatusTextShadow.Draw();
                StatusText.Draw();
            }

            if (DrawDangerPolygon)
            {
                foreach (
                    var pol in
                        Geometry.ClipPolygons(SkillshotDetector.ActiveSkillshots.Select(c => c.ToPolygon()))
                            .ToPolygons())
                {
                    pol.DrawPolygon(Color.Red, 3);
                }
            }
        }

        private void CacheSkillshots()
        {
            Skillshots =
                (DodgeDangerousOnly
                    ? SkillshotDetector.ActiveSkillshots.Where(c => c.SpellData.IsDangerous)
                    : SkillshotDetector.ActiveSkillshots).ToArray();

            _skillshotPolygonCache.Clear();
            Polygons = Skillshots.Select(c =>
            {
                var pol = c.ToPolygon();
                _skillshotPolygonCache.Add(c, pol);

                return pol;
            }).ToArray();
            ClippedPolygons = Geometry.ClipPolygons(Polygons).ToPolygons();
        }

        public bool IsPointSafe(Vector2 point)
        {
            return !ClippedPolygons.Any(p => p.IsInside(point));
        }

        public bool IsPathSafe(Vector2[] path)
        {
            for (var i = 0; i < path.Length - 1; i++)
            {
                var start = path[i];
                var end = path[i + 1];

                if (
                    ClippedPolygons.Any(
                        p => p.IsInside(end) || p.IsInside(start) || p.IsIntersectingWithLineSegment(start, end)))
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsPathSafe(Vector3[] path)
        {
            return IsPathSafe(path.ToVector2());
        }

        public bool IsHeroInDanger(AIHeroClient hero = null)
        {
            hero = hero ?? Player.Instance;
            return !IsPointSafe(hero.ServerPosition.To2D());
        }

        public int GetTimeAvailable(AIHeroClient hero = null)
        {
            hero = hero ?? Player.Instance;
            var skillshots = Skillshots.Where(c => _skillshotPolygonCache[c].IsInside(hero.Position)).ToArray();

            if (!skillshots.Any())
                return short.MaxValue;

            var times =
                skillshots.Select(c => c.GetAvailableTime(hero))
                    .Where(t => t > 0)
                    .OrderByDescending(t => t);

            return times.Any() ? times.Last() : short.MaxValue;
        }

        public int GetDangerValue(AIHeroClient hero = null)
        {
            hero = hero ?? Player.Instance;
            var skillshots = Skillshots.Where(c => _skillshotPolygonCache[c].IsInside(hero.Position)).ToArray();

            if (!skillshots.Any())
                return 0;

            var values = skillshots.Select(c => c.SpellData.DangerValue).OrderByDescending(t => t);
            return values.Any() ? values.First() : 0;
        }

        public EvadeResult CalculateEvade(Vector2 anchor)
        {
            var playerPos = Player.Instance.ServerPosition.To2D();
            var polygons = ClippedPolygons.Where(p => p.IsInside(playerPos)).ToArray();
            var maxTime = GetTimeAvailable();
            var time = maxTime - (Game.Ping/2 + ServerBuffer);
            var moveRadius = (time/1000F)*Player.Instance.MoveSpeed;
            var segments = new List<Vector2[]>();

            foreach (var pol in polygons)
            {
                for (var i = 0; i < pol.Points.Count; i++)
                {
                    var start = pol.Points[i];
                    var end = i == pol.Points.Count - 1 ? pol.Points[0] : pol.Points[i + 1];

                    var intersections =
                        Utils.GetLineCircleIntersectionPoints(playerPos, moveRadius, start, end)
                            .Where(p => p.IsInLineSegment(start, end))
                            .ToList();

                    if (intersections.Count == 0)
                    {
                        if (start.Distance(playerPos, true) < moveRadius.Pow() &&
                            end.Distance(playerPos, true) < moveRadius.Pow())
                        {
                            intersections = new[] {start, end}.ToList();
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (intersections.Count == 1)
                    {
                        intersections.Add(playerPos.Distance(start, true) > playerPos.Distance(end, true)
                            ? end
                            : start);
                    }

                    segments.Add(intersections.ToArray());
                }
            }

            if (!segments.Any()) //not enough time
            {
                var polPoints = polygons.Select(pol => pol.ToDetailedPolygon()).SelectMany(pol => pol.Points).ToList();
                polPoints.Sort((p1, p2) => p1.Distance(playerPos, true).CompareTo(p2.Distance(playerPos, true)));

                return polPoints.Any()
                    ? new EvadeResult(this, polPoints.First(), anchor, maxTime, time, false)
                    : new EvadeResult(this, playerPos, anchor, maxTime, time, false);
            }

            const int maxdist = 2000;
            const int division = 30;
            var points = new List<Vector2>();

            foreach (var segment in segments)
            {
                var dist = segment[0].Distance(segment[1]);
                if (dist > maxdist)
                {
                    segment[0] = segment[0].Extend(segment[1], dist/2 - maxdist/2);
                    segment[1] = segment[1].Extend(segment[1], dist/2 - maxdist/2);
                    dist = maxdist;
                }

                var step = maxdist/division;
                var count = dist/step;

                for (var i = 0; i < count; i++)
                {
                    var point = segment[0].Extend(segment[1], i*step);
                    if (!point.IsWall() &&
                        !Polygons.Where(pol => !pol.IsInside(playerPos))
                            .Any(pol => pol.IsIntersectingWithLineSegment(playerPos, point)) &&
                        Player.Instance.GetPath(point.To3DWorld(), true).Length <= 3)
                    {
                        points.Add(point);
                    }
                }
            }

            if (!points.Any())
            {
                return new EvadeResult(this, Vector2.Zero, anchor, maxTime, time, true);
            }

            var evadePoint = points.OrderByDescending(p => p.Distance(anchor) + p.Distance(playerPos)).Last();
            return new EvadeResult(this, evadePoint, anchor, maxTime, time, true);
        }

        public Vector2[] GetPath(Vector2 start, Vector2 end)
        {
            const int extraWidth = 30;
            var walkPolygons = Geometry.ClipPolygons(Skillshots.Select(c => c.ToPolygon(extraWidth))).ToPolygons();

            //if (walkPolygons.Any(pol => pol.IsInside(start)))
            //{
            //    Chat.Print("start");
            //    var polPoints =
            //        Geometry.ClipPolygons(
            //            SkillshotDetector.DetectedSkillshots.Where(c => c.IsValid)
            //                .Select(c => c.ToPolygon(extraWidth))
            //                .ToList())
            //            .ToPolygons()
            //            .Where(pol => pol.IsInside(start))
            //            .SelectMany(pol => pol.Points)
            //            .ToList();
            //    polPoints.Sort((p1, p2) => p1.Distance(start, true).CompareTo(p2.Distance(start, true)));
            //    start = polPoints.First().Extend(start, -150);
            //}

            if (walkPolygons.Any(pol => pol.IsInside(end)))
            {
                var polPoints =
                    Geometry.ClipPolygons(
                        SkillshotDetector.DetectedSkillshots.Where(c => c.IsValid)
                            .Select(c => c.ToPolygon(extraWidth))
                            .ToList())
                        .ToPolygons()
                        .Where(pol => pol.IsInside(end))
                        .SelectMany(pol => pol.Points)
                        .ToList();
                polPoints.Sort((p1, p2) => p1.Distance(end, true).CompareTo(p2.Distance(end, true)));
                end = polPoints.First().Extend(end, -extraWidth);
            }

            var ritoPath =
                Player.Instance.GetPath(start.To3DWorld(), end.To3DWorld()).ToArray().ToVector2().ToList();
            var pathPoints = new List<Vector2>();
            var polygonDictionary = new Dictionary<Vector2, Geometry.Polygon>();
            for (var i = 0; i < ritoPath.Count - 1; i++)
            {
                var lineStart = ritoPath[i];
                var lineEnd = ritoPath[i + 1];

                foreach (var pol in walkPolygons)
                {
                    var intersectionPoints = pol.GetIntersectionPointsWithLineSegment(lineStart, lineEnd);
                    foreach (var p in intersectionPoints)
                    {
                        if (!polygonDictionary.ContainsKey(p))
                        {
                            polygonDictionary.Add(p, pol);
                            pathPoints.Add(p);
                        }
                    }
                }
            }
            ritoPath.RemoveAll(p => walkPolygons.Any(pol => pol.IsInside(p)));
            pathPoints.AddRange(ritoPath);
            pathPoints.SortPath(start);

            var path = new List<Vector2>();

            while (pathPoints.Count > 0)
            {
                if (pathPoints.Count == 1)
                {
                    path.Add(pathPoints[0]);
                    break;
                }

                var current = pathPoints[0];
                var next = pathPoints[1];

                Geometry.Polygon pol1;
                Geometry.Polygon pol2;

                if (polygonDictionary.TryGetValue(current, out pol1) && polygonDictionary.TryGetValue(next, out pol2) &&
                    pol1.Equals(pol2))
                {
                    var detailedPolygon = pol1.ToDetailedPolygon();
                    detailedPolygon.Points.Sort(
                        (p1, p2) => p1.Distance(current, true).CompareTo(p2.Distance(current, true)));
                    current = detailedPolygon.Points.First();

                    detailedPolygon.Points.Sort((p1, p2) => p1.Distance(next, true).CompareTo(p2.Distance(next, true)));
                    next = detailedPolygon.Points.First();

                    detailedPolygon = pol1.ToDetailedPolygon();
                    var index = detailedPolygon.Points.FindIndex(p => p == current);
                    var linkedList = new LinkedList<Vector2>(detailedPolygon.Points, index);

                    var nextPath = new List<Vector2>();
                    var previousPath = new List<Vector2>();
                    var nextLength = 0F;
                    var previousLength = 0F;
                    var nextWall = false;
                    var previousWall = false;

                    while (true)
                    {
                        var c = linkedList.Next();

                        if (c.IsWall())
                        {
                            nextWall = true;
                            break;
                        }

                        nextPath.Add(c);

                        if (nextPath.Count > 1)
                            nextLength += nextPath[nextPath.Count - 2].Distance(c, true);

                        if (c == next)
                            break;
                    }

                    linkedList.Index = index;
                    while (true)
                    {
                        var c = linkedList.Previous();

                        if (c.IsWall())
                        {
                            previousWall = true;
                            break;
                        }

                        previousPath.Add(c);

                        if (previousPath.Count > 1)
                            previousLength += previousPath[previousPath.Count - 2].Distance(c, true);

                        if (c == next)
                            break;
                    }

                    var shortest = nextWall && previousWall
                        ? (nextLength > previousLength ? nextPath : previousPath)
                        : (nextWall || previousWall
                            ? (nextWall ? previousPath : nextPath)
                            : nextLength < previousLength ? nextPath : previousPath);
                    path.AddRange(shortest);

                    if (previousWall && nextWall)
                        break;
                }
                else
                {
                    path.Add(current);
                    path.Add(next);
                }

                pathPoints.RemoveRange(0, 2);
            }

            return path.ToArray();
        }

        public bool IsHeroPathSafe(EvadeResult evade, Vector3[] desiredPath, AIHeroClient hero = null)
        {
            hero = hero ?? Player.Instance;

            var path = (desiredPath ?? hero.RealPath()).ToVector2();
            var polygons = ClippedPolygons;
            var points = new List<Vector2>();

            for (var i = 0; i < path.Length - 1; i++)
            {
                var start = path[i];
                var end = path[i + 1];

                foreach (var pol in polygons)
                {
                    var intersections = pol.GetIntersectionPointsWithLineSegment(start, end);
                    if (intersections.Length > 0 && !pol.IsInside(hero))
                    {
                        return false;
                    }

                    points.AddRange(intersections);
                }
            }

            if (points.Count == 1)
            {
                return hero.WalkingTime(points[0]) <= evade.TotalTimeAvailable + 70;
            }

            return false;
        }

        public bool DoEvade(AIHeroClient hero = null, Vector3[] desiredPath = null)
        {
            if (!EvadeEnabled)
            {
                LastEvadeResult = null;
                AutoPathing.StopPath();
                return false;
            }

            hero = hero ?? Player.Instance;

            if (IsHeroInDanger())
            {
                if (LastEvadeResult != null && (!IsPointSafe(LastEvadeResult.EvadePoint) || LastEvadeResult.Expired()))
                {
                    LastEvadeResult = null;
                }

                var evade = CalculateEvade(LastIssueOrderPos);
                if (evade.IsValid && evade.EnoughTime)
                {
                    if (LastEvadeResult == null ||
                        (LastEvadeResult.EvadePoint.Distance(evade.EvadePoint, true) > 200.Pow()))
                    {
                        LastEvadeResult = evade;
                    }

                    if (IsHeroPathSafe(evade, desiredPath))
                    {
                        LastEvadeResult = null;
                    }
                }
                else
                {
                    if (!evade.EnoughTime && LastEvadeResult == null && !IsHeroPathSafe(evade, desiredPath))
                    {
                        return EvadeSpellManager.ProcessFlash(this);
                    }
                }

                if (LastEvadeResult != null)
                {
                    if (!hero.IsMovingTowards(LastEvadeResult.EvadePoint))
                    {
                        Player.IssueOrder(GameObjectOrder.MoveTo, LastEvadeResult.EvadePoint.To3DWorld(), false);
                    }

                    return true;
                }
            }
            else if (!IsPathSafe(hero.RealPath()) || (desiredPath != null && !IsPathSafe(desiredPath)))
            {
                var path = GetPath(hero.Position.To2D(), LastIssueOrderPos);
                if (path.Length > 0 && AutoPathing.Destination.Distance(path.Last(), true) > 50.Pow())
                {
                    AutoPathing.DoPath(path);
                }

                LastEvadeResult = null;
                return true;
            }
            else
            {
                AutoPathing.StopPath();
                LastEvadeResult = null;
            }

            return false;
        }

        public class EvadeResult
        {
            private EvadePlus Evade;

            public int Time { get; set; }
            public Vector2 PlayerPos { get; set; }
            public Vector2 EvadePoint { get; set; }
            public Vector2 AnchorPoint { get; set; }
            public int TimeAvailable { get; set; }
            public int TotalTimeAvailable { get; set; }
            public bool EnoughTime { get; set; }

            public bool IsValid
            {
                get { return !EvadePoint.IsZero; }
            }

            public EvadeResult(EvadePlus evade, Vector2 evadePoint, Vector2 anchorPoint, int totalTimeAvailable,
                int timeAvailable,
                bool enoughTime)
            {
                Evade = evade;
                PlayerPos = Player.Instance.Position.To2D();
                Time = Environment.TickCount;

                EvadePoint = evadePoint.Extend(PlayerPos, -15);
                AnchorPoint = anchorPoint;
                TotalTimeAvailable = totalTimeAvailable;
                TimeAvailable = timeAvailable;
                EnoughTime = enoughTime;

                if (Evade.ExtraEvadeRange > 0)
                {
                    var newPoint = EvadePoint.Extend(PlayerPos,
                        -(Evade.RandomizeExtraEvadeRange
                            ? Utils.Random.Next(Evade.ExtraEvadeRange/3, Evade.ExtraEvadeRange)
                            : Evade.ExtraEvadeRange));
                    if (Evade.IsPointSafe(newPoint))
                    {
                        EvadePoint = newPoint;
                    }
                }
            }

            public bool Expired(int time = 3500)
            {
                return Elapsed(time);
            }

            public bool Elapsed(int time)
            {
                return Environment.TickCount > Time + time;
            }
        }
    }
}