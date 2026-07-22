using BepInEx;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Zorro.Settings;

namespace CapsulePath
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class CapsulePathPlugin : BaseUnityPlugin
    {
        private const int   SplineSteps   = 10;    // max spline points per polyline segment
        private const float SplineStepLength = 0.25f; // target spacing of spline points (meters)
        private const int   ArrowCount    = 8;
        private const float ArrowSpeedMps = 5f;    // world-space meters per second
        private const float ArrowSize     = 0.55f; // chevron arm length (world units)
        private const float ArrowAngle    = 38f;   // chevron opening angle (degrees)

        // Paths shorter than this draw the line but no arrows: AnimateArrows never
        // runs for them, so enabling arrows would leave stale chevrons in the air.
        private const float MinAnimatedPathLength = 0.1f; // meters

        // NavMesh corners closer together than this are collapsed before smoothing;
        // near-coincident control points are what made the spline tie knots.
        private const float MinCornerSpacing = 0.05f; // meters

        // Funnel corners only mark turns, so the straight chord between them cuts
        // through stairs/slopes. Sample intermediate points every DensifyStep and
        // re-project them onto the NavMesh so the polyline follows the surface.
        private const float DensifyStep       = 0.75f; // meters between surface samples
        private const float SurfaceSnapRadius = 1.5f;  // NavMesh.SamplePosition search radius
        private const float MaxLateralSnapSqr = 0.5f * 0.5f; // reject sideways snaps (m^2)

        // The NavMesh is baked for walking monsters; players climb over low
        // obstacles and NavMesh gaps without noticing. When a path comes back
        // partial, probe past the blockage along the route direction and bridge
        // onto the next NavMesh piece with a climb arc - but only if nothing
        // taller than ClimbHeight (about half the player's body) is in the way.
        private const float ClimbHeight      = 1.0f;  // max climbable obstacle height (m)
        private const float MaxDropHeight    = 3.0f;  // max drop-down when bridging (m)
        private const float BridgeProbeStep  = 0.75f; // spacing of probes past a blockage
        private const float BridgeProbeMax   = 5f;    // how far past a blockage to search
        private const float BridgeSnapRadius = 3.5f;  // vertical reach of resume sampling
        private const float MaxProbeDriftSqr = 1.2f * 1.2f; // reject sideways resume snaps (m^2)
        private const int   MaxBridges       = 3;     // climb arcs per route

        // The nearest NavMesh sample can land on a disconnected island (prop tops,
        // raised ledges) and produce a bogus "blocked" route; starts a couple of
        // meters to the side often sit on the main mesh. Try a ring of candidates.
        private const float StartSnapRadius   = 1.6f;
        private const float StartRingRadius1  = 1.6f;
        private const float StartRingRadius2  = 3.2f;
        private const float EndReachTolerance = 1.6f; // arriving this close counts as reaching the capsule

        private static readonly Color PathColor  = new Color(0.15f, 1f, 0.25f, 0.45f);
        private static readonly Color ArrowColor = new Color(0.2f, 1f, 0.3f, 1f);

        // Keys are rebindable in Settings -> MODS (see Settings.cs); the defaults
        // below are only used until the game's SettingsHandler exists.
        private CapsulePathRecalculateKeySetting _recalculateKeySetting;
        private CapsulePathToggleKeySetting      _toggleKeySetting;

        private KeyCode RecalculateKey => ResolveKey(ref _recalculateKeySetting, KeyCode.K);
        private KeyCode ToggleViewKey  => ResolveKey(ref _toggleKeySetting, KeyCode.O);

        private static KeyCode ResolveKey<T>(ref T cached, KeyCode fallback) where T : KeyCodeSetting
        {
            if (cached == null)
                cached = GameHandler.Instance?.SettingsHandler?.GetSetting<T>();
            return cached != null ? cached.Keycode() : fallback;
        }

        private CapsulePathHideFromCameraSetting _hideFromCameraSetting;

        private bool HideFromCamera
        {
            get
            {
                if (_hideFromCameraSetting == null)
                    _hideFromCameraSetting = GameHandler.Instance?.SettingsHandler?.GetSetting<CapsulePathHideFromCameraSetting>();
                return _hideFromCameraSetting == null || _hideFromCameraSetting.Value;
            }
        }

        private CapsulePathShowStatusSetting _showStatusSetting;

        private bool ShowStatusHud
        {
            get
            {
                if (_showStatusSetting == null)
                    _showStatusSetting = GameHandler.Instance?.SettingsHandler?.GetSetting<CapsulePathShowStatusSetting>();
                return _showStatusSetting == null || _showStatusSetting.Value;
            }
        }

        private readonly NavMeshPath       _navPath = new NavMeshPath();
        private          LineRenderer      _line;
        private readonly List<LineRenderer> _arrows = new List<LineRenderer>();

        private Vector3[] _smoothedPath;
        private float[]   _segLengths;
        private float     _totalLength;
        private float     _arrowPhase;

        private bool    _pathVisible = true;
        private Vector3? _capsulePos;   // camera-based fallback anchor (see TrackCapsulePosition)
        private DivingBell _cachedBell; // preferred anchor: the actual capsule object

        // Per-camera "is this a VideoCamera lens?" cache; cleared on scene change.
        private readonly Dictionary<Camera, bool> _isRecordingCamera = new Dictionary<Camera, bool>();
        private bool _hiddenForRecording;

        // On-screen status marker (OnGUI): a short message shown when a key is
        // pressed so the player gets feedback even when no path is drawn. IMGUI is
        // composited to the screen only, never into a VideoCamera RenderTexture,
        // so this never leaks into recordings.
        private const float StatusDuration = 3.2f; // seconds visible
        private const float StatusFade     = 0.6f; // seconds of fade-out at the end
        private string    _statusText;
        private float     _statusShownAt = -100f;
        private GUIStyle  _statusStyle;
        private Texture2D _statusTex;

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            EnsureLineRenderer();
            EnsureArrows();
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering   += OnEndCameraRendering;
            Logger.LogInfo($"{PluginInfo.Name} loaded. Defaults: K=calculate path, O=toggle path (rebindable in Settings -> MODS).");
        }

        private void Update()
        {
            TrackCapsulePosition();

            if (GameplayInputActive())
            {
                if (Input.GetKeyDown(ToggleViewKey))
                {
                    _pathVisible = !_pathVisible;
                    if (_line != null) _line.enabled = _pathVisible;
                    SetArrowsVisible(_pathVisible);
                    if (_smoothedPath == null)
                        ShowStatus($"CapsulePath: press [{RecalculateKey}] to find the capsule");
                    else
                        ShowStatus(_pathVisible ? "CapsulePath: path shown" : "CapsulePath: path hidden");
                    Logger.LogInfo($"[CapsulePath] Visibility: {(_pathVisible ? "ON" : "OFF")}");
                }

                if (Input.GetKeyDown(RecalculateKey))
                    RecalculatePath();
            }

            if (_smoothedPath != null && _pathVisible && _totalLength > MinAnimatedPathLength)
                AnimateArrows();
        }

        // The game unlocks the cursor exactly when a menu/modal/terminal is up
        // (CursorHandler), which also covers the settings key-rebind flow;
        // CanTakeInput() additionally covers menus opened on the gamepad scheme,
        // where the cursor stays locked. Vanilla keybinds use the same gate.
        private static bool GameplayInputActive()
        {
            return Cursor.lockState == CursorLockMode.Locked && GlobalInputHandler.CanTakeInput();
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering   -= OnEndCameraRendering;
            foreach (var lr in _arrows)
                if (lr != null) Destroy(lr.gameObject);
            _arrows.Clear();
            if (_line != null) Destroy(_line.gameObject);
            if (_statusTex != null) Destroy(_statusTex);
        }

        // ── hide from in-game camera footage ──────────────────────────────────
        // The camcorder records through VideoCamera.m_camera (a separate Unity
        // camera rendering to a RenderTexture). Hiding the path only while that
        // camera renders keeps it visible on screen but out of the recording
        // and off the camcorder's live viewfinder.

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!_pathVisible || _smoothedPath == null || !HideFromCamera) return;
            if (!IsRecordingCamera(cam)) return;

            _hiddenForRecording = true;
            if (_line != null) _line.enabled = false;
            HideArrows();
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!_hiddenForRecording || !IsRecordingCamera(cam)) return;

            _hiddenForRecording = false;
            if (_line != null) _line.enabled = _pathVisible;
            SetArrowsVisible(_pathVisible);
        }

        private bool IsRecordingCamera(Camera cam)
        {
            if (!_isRecordingCamera.TryGetValue(cam, out bool isRecording))
            {
                isRecording = cam.GetComponentInParent<VideoCamera>() != null;
                _isRecordingCamera[cam] = isRecording;
            }
            return isRecording;
        }

        // ── scene / capsule tracking ───────────────────────────────────────────

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            _capsulePos = null;
            _cachedBell = null;
            _isRecordingCamera.Clear();
            ClearPath();
            Logger.LogInfo($"[CapsulePath] Scene changed -> state reset ({current.name})");
        }

        private void TrackCapsulePosition()
        {
            // Latch the anchor once per scene, on the first frame bots exist (dive
            // start, players still at the capsule). Bot count can legitimately dip
            // back to 0 mid-run (first wave killed before the delayed second wave
            // spawns), so a 0 -> N edge is NOT a reliable "dive started" signal;
            // only a scene change resets the latch.
            if (_capsulePos.HasValue) return;
            if ((BotHandler.instance?.bots?.Count ?? 0) == 0) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            _capsulePos = cam.transform.position;
            Logger.LogInfo($"[CapsulePath] Capsule anchor recorded: {_capsulePos.Value}");
        }

        // ── path calculation ───────────────────────────────────────────────────

        private void RecalculatePath()
        {
            if (!TryGetCapsuleTarget(out Vector3 capsule, out string capsuleSrc))
            {
                ShowStatus("CapsulePath: capsule not found yet (start the dive)");
                Logger.LogWarning("[CapsulePath] Capsule target unknown (no diving bell, no anchor).");
                return;
            }

            Player local = Player.localPlayer;
            if (local == null)
            {
                ShowStatus("CapsulePath: player not ready");
                Logger.LogWarning("[CapsulePath] Local player not found.");
                return;
            }

            if (!TryGetLocalAvatarPosition(local, out Vector3 playerNow, out string source))
            {
                ShowStatus("CapsulePath: player position unknown");
                Logger.LogWarning("[CapsulePath] Could not resolve dynamic local avatar position.");
                return;
            }

            Vector3 end = capsule;
            if (!TrySampleToNavMesh(ref end))
            {
                ClearPath();
                ApplyPath(new[] { playerNow + Vector3.up * 0.2f, capsule + Vector3.up * 0.2f });
                ShowStatus(WithVisibilityHint("CapsulePath: direct line (off NavMesh)"));
                Logger.LogWarning("[CapsulePath] Could not sample capsule to NavMesh. Using direct fallback.");
                return;
            }

            RouteResult route = ComputeRoute(playerNow, end);
            if (route == null)
            {
                ClearPath();
                ApplyPath(new[] { playerNow + Vector3.up * 0.2f, capsule + Vector3.up * 0.2f });
                ShowStatus(WithVisibilityHint("CapsulePath: no route - direct line"));
                Logger.LogWarning("[CapsulePath] Path not found/invalid. Using direct fallback.");
                return;
            }

            var pts = new List<Vector3>(128);
            for (int i = 0; i < route.Segments.Count; i++)
            {
                Vector3[] segCorners = DropCoincidentCorners(route.Segments[i], MinCornerSpacing);
                Vector3[] dense      = DensifyOnNavMesh(segCorners);
                if (i > 0 && pts.Count > 0 && dense.Length > 0)
                {
                    // Climb arc over the obstacle between the two NavMesh pieces.
                    // Skip the raised midpoint on a hairpin (next piece starts behind
                    // the travel direction) - arcing a reversal reads as a knot.
                    Vector3 a = pts[pts.Count - 1];
                    Vector3 b = dense[0];
                    bool hairpin = false;
                    if (pts.Count >= 2)
                    {
                        Vector3 inDir = a - pts[pts.Count - 2];
                        Vector3 abDir = b - a;
                        inDir.y = 0f;
                        abDir.y = 0f;
                        hairpin = Vector3.Dot(inDir, abDir) < 0f;
                    }
                    if (!hairpin)
                        pts.Add((a + b) * 0.5f + Vector3.up * (Mathf.Abs(b.y - a.y) * 0.5f + 0.45f));
                }
                pts.AddRange(dense);
            }

            Vector3[] lifted = LiftCorners(pts.ToArray(), 0.12f);
            Vector3[] smooth = CatmullRom(lifted, SplineSteps);
            ApplyPath(smooth);

            if (route.Complete)
                ShowStatus(WithVisibilityHint(route.Bridges > 0
                    ? $"CapsulePath: {route.NavLength:F0} m to capsule (short climb on the way)"
                    : $"CapsulePath: {route.NavLength:F0} m to capsule"));
            else
                ShowStatus(WithVisibilityHint($"CapsulePath: partial route ~{route.NavLength:F0} m (capsule may be blocked)"));

            if (!route.Complete)
                Logger.LogWarning("[CapsulePath] Route is partial - it stops at the closest reachable point, not at the capsule.");
            Logger.LogInfo($"[CapsulePath] Route: segments={route.Segments.Count}, bridges={route.Bridges}, complete={route.Complete}, pts={smooth.Length}, len={route.NavLength:F1}m, src={source}, tgt={capsuleSrc}");
        }

        // ── routing (start candidates + climb bridging) ────────────────────────

        private sealed class RouteResult
        {
            public readonly List<Vector3[]> Segments = new List<Vector3[]>();
            public bool  Complete;
            public int   Bridges;
            public float NavLength;
            public float RemainingToEnd = float.MaxValue;
        }

        private RouteResult ComputeRoute(Vector3 rawStart, Vector3 end)
        {
            RouteResult best = null;
            foreach (Vector3 start in StartCandidates(rawStart))
            {
                RouteResult r = RouteFrom(start, end);
                if (r == null) continue;
                if (best == null || RouteBetter(r, best)) best = r;
                if (best.Complete) break;
            }
            return best;
        }

        private static IEnumerable<Vector3> StartCandidates(Vector3 raw)
        {
            var yielded = new List<Vector3>(20);
            bool Fresh(Vector3 p)
            {
                foreach (Vector3 q in yielded)
                    if ((p - q).sqrMagnitude < 0.25f) return false;
                yielded.Add(p);
                return true;
            }

            if (NavMesh.SamplePosition(raw, out NavMeshHit direct, StartSnapRadius, NavMesh.AllAreas) && Fresh(direct.position))
                yield return direct.position;

            foreach (float radius in new[] { StartRingRadius1, StartRingRadius2 })
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * Mathf.PI * 2f / 8f;
                    Vector3 probe = raw + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * radius;
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, StartSnapRadius, NavMesh.AllAreas) && Fresh(hit.position))
                        yield return hit.position;
                }

            if (NavMesh.SamplePosition(raw, out NavMeshHit wide, 4f, NavMesh.AllAreas) && Fresh(wide.position))
                yield return wide.position;
        }

        private RouteResult RouteFrom(Vector3 start, Vector3 end)
        {
            var result = new RouteResult();
            Vector3 cur = start;
            float prevRemaining = float.MaxValue;
            for (int hop = 0; hop <= MaxBridges; hop++)
            {
                if (!NavMesh.CalculatePath(cur, end, NavMesh.AllAreas, _navPath)) break;
                Vector3[] corners = _navPath.corners;
                if (corners == null || corners.Length < 2) break;
                if (_navPath.status == NavMeshPathStatus.PathInvalid) break;

                Vector3 reached   = corners[corners.Length - 1];
                float   remaining = Vector3.Distance(reached, end);

                // A bridged continuation must make real progress toward the capsule.
                // Without this, resumes onto micro-islands ping-pong back and forth
                // and the smoothed line ties knots in mid-air.
                if (hop > 0 && remaining > prevRemaining - 1f) break;

                result.Segments.Add(corners);
                result.NavLength     += PolylineLength(corners);
                result.RemainingToEnd = remaining;
                prevRemaining         = remaining;

                if (_navPath.status == NavMeshPathStatus.PathComplete || remaining <= EndReachTolerance)
                {
                    result.Complete = true;
                    break;
                }

                if (!TryFindBridgeResume(reached, end, out Vector3 resume)) break;
                cur = resume;
            }
            if (result.Segments.Count == 0) return null;
            result.Bridges = result.Segments.Count - 1;
            return result;
        }

        private static bool RouteBetter(RouteResult a, RouteResult b)
        {
            if (a.Complete != b.Complete) return a.Complete;
            return a.RemainingToEnd < b.RemainingToEnd - 0.25f;
        }

        private static bool TryFindBridgeResume(Vector3 from, Vector3 target, out Vector3 resume)
        {
            Vector3 dir = target - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) { resume = default; return false; }
            dir.Normalize();

            float fromDist = HorizontalDistance(from, target);
            for (float d = BridgeProbeStep; d <= BridgeProbeMax; d += BridgeProbeStep)
            {
                Vector3 probe = from + dir * d + Vector3.up * 0.4f;
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, BridgeSnapRadius, NavMesh.AllAreas))
                    continue;

                Vector3 drift = hit.position - probe;
                drift.y = 0f;
                if (drift.sqrMagnitude > MaxProbeDriftSqr) continue;

                float rise = hit.position.y - from.y;
                if (rise > ClimbHeight || rise < -MaxDropHeight) continue;

                // Must make real progress toward the capsule, not hop sideways.
                if (HorizontalDistance(hit.position, target) > fromDist - 0.5f) continue;

                // The obstacle must be vaultable: nothing may block at climb height.
                // This is what keeps bridges from ever crossing real walls.
                if (Physics.Linecast(from + Vector3.up * (ClimbHeight + 0.15f),
                                     hit.position + Vector3.up * (ClimbHeight + 0.15f),
                                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;

                resume = hit.position;
                return true;
            }
            resume = default;
            return false;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float PolylineLength(Vector3[] pts)
        {
            float len = 0f;
            for (int i = 1; i < pts.Length; i++)
                len += Vector3.Distance(pts[i - 1], pts[i]);
            return len;
        }

        private string WithVisibilityHint(string msg)
        {
            return _pathVisible ? msg : msg + $"  (hidden - [{ToggleViewKey}] to show)";
        }

        // ── capsule target resolution ──────────────────────────────────────────
        // Prefer the real DivingBell object over the camera position latched at
        // dive start: the camera anchor is the player's head wherever they stood,
        // so NavMesh sampling could land a few meters off the actual capsule.

        private bool TryGetCapsuleTarget(out Vector3 target, out string source)
        {
            DivingBell bell = ResolveDivingBell();
            if (bell != null)
            {
                target = DivingBellAnchor(bell);
                source = "DivingBell";
                return true;
            }
            if (_capsulePos.HasValue)
            {
                target = _capsulePos.Value;
                source = "camera-anchor";
                return true;
            }
            target = default;
            source = "none";
            return false;
        }

        private DivingBell ResolveDivingBell()
        {
            if (_cachedBell != null) return _cachedBell;
            // The underground capsule is the DivingBell with onSurface == false;
            // ignore the surface (house) bell so the mod stays a dive-only helper.
            foreach (DivingBell b in Object.FindObjectsByType<DivingBell>(FindObjectsSortMode.None))
                if (b != null && !b.onSurface) { _cachedBell = b; break; }
            return _cachedBell;
        }

        private static Vector3 DivingBellAnchor(DivingBell bell)
        {
            // The player-detector transforms mark where players stand inside the
            // bell - the most reliable "walk to here" target. Average them; fall
            // back to the bell's own transform if none are wired up.
            Transform[] det = bell.playerDetector != null ? bell.playerDetector.m_detectors : null;
            if (det != null && det.Length > 0)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (Transform t in det)
                    if (t != null) { sum += t.position; n++; }
                if (n > 0) return sum / n;
            }
            return bell.transform.position;
        }

        // ── Catmull-Rom smoothing ──────────────────────────────────────────────

        private static Vector3[] LiftCorners(Vector3[] corners, float up)
        {
            var r = new Vector3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
                r[i] = corners[i] + Vector3.up * up;
            return r;
        }

        private static Vector3[] DropCoincidentCorners(Vector3[] corners, float minDist)
        {
            if (corners.Length < 3) return corners;
            var kept = new List<Vector3>(corners.Length) { corners[0] };
            for (int i = 1; i < corners.Length - 1; i++)
                if (Vector3.Distance(kept[kept.Count - 1], corners[i]) >= minDist)
                    kept.Add(corners[i]);
            kept.Add(corners[corners.Length - 1]);
            return kept.ToArray();
        }

        private static Vector3[] DensifyOnNavMesh(Vector3[] corners)
        {
            var pts = new List<Vector3>(corners.Length * 4) { corners[0] };
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 a = corners[i - 1], b = corners[i];
                int extra = Mathf.FloorToInt(Vector3.Distance(a, b) / DensifyStep);
                for (int s = 1; s <= extra; s++)
                {
                    Vector3 p = Vector3.Lerp(a, b, s / (float)(extra + 1));
                    if (NavMesh.SamplePosition(p, out NavMeshHit hit, SurfaceSnapRadius, NavMesh.AllAreas))
                    {
                        // Accept mostly-vertical corrections only: pulling the point
                        // sideways onto a neighboring surface would warp the route.
                        Vector3 lateral = hit.position - p;
                        lateral.y = 0f;
                        if (lateral.sqrMagnitude <= MaxLateralSnapSqr)
                            p = hit.position;
                    }
                    pts.Add(p);
                }
                pts.Add(b);
            }
            return pts.ToArray();
        }

        private static Vector3[] CatmullRom(Vector3[] pts, int maxSteps)
        {
            if (pts.Length < 2) return pts;

            // Duplicate endpoints so the spline starts/ends tangent to the first/last segment
            // without overshooting (avoids the knot that mirroring creates).
            Vector3[] ext = new Vector3[pts.Length + 2];
            ext[0] = pts[0];
            for (int i = 0; i < pts.Length; i++) ext[i + 1] = pts[i];
            ext[ext.Length - 1] = pts[pts.Length - 1];

            var result = new List<Vector3>(pts.Length * 4);
            for (int i = 1; i < ext.Length - 2; i++)
            {
                // Densified segments are short; scale point count to segment length
                // instead of spending maxSteps on every one.
                float segLen = Vector3.Distance(ext[i], ext[i + 1]);
                int steps = Mathf.Clamp(Mathf.CeilToInt(segLen / SplineStepLength), 2, maxSteps);
                for (int s = 0; s < steps; s++)
                {
                    float t = s / (float)steps;
                    result.Add(CatmullRomPoint(ext[i - 1], ext[i], ext[i + 1], ext[i + 2], t));
                }
            }
            result.Add(pts[pts.Length - 1]);
            return result.ToArray();
        }

        // Centripetal Catmull-Rom (Barry-Goldman, alpha = 0.5). Unlike the uniform
        // variant, it cannot form loops or cusps inside a segment - uniform
        // parameterization was what tied knots at tight NavMesh corner clusters.
        private static Vector3 CatmullRomPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            // Real corners are >= MinCornerSpacing apart, so their knot intervals are
            // >= sqrt(0.05) ~ 0.22; only the duplicated endpoints hit this floor. Keeping
            // it large bounds the Barry-Goldman coefficients (~30 max), which keeps
            // float cancellation error negligible at world-scale coordinates.
            const float minKnot = 0.2f;
            float t0 = 0f;
            float t1 = t0 + Mathf.Max(Mathf.Sqrt(Vector3.Distance(p0, p1)), minKnot);
            float t2 = t1 + Mathf.Max(Mathf.Sqrt(Vector3.Distance(p1, p2)), minKnot);
            float t3 = t2 + Mathf.Max(Mathf.Sqrt(Vector3.Distance(p2, p3)), minKnot);

            float u = Mathf.Lerp(t1, t2, t);

            Vector3 a1 = (t1 - u) / (t1 - t0) * p0 + (u - t0) / (t1 - t0) * p1;
            Vector3 a2 = (t2 - u) / (t2 - t1) * p1 + (u - t1) / (t2 - t1) * p2;
            Vector3 a3 = (t3 - u) / (t3 - t2) * p2 + (u - t2) / (t3 - t2) * p3;

            Vector3 b1 = (t2 - u) / (t2 - t0) * a1 + (u - t0) / (t2 - t0) * a2;
            Vector3 b2 = (t3 - u) / (t3 - t1) * a2 + (u - t1) / (t3 - t1) * a3;

            return (t2 - u) / (t2 - t1) * b1 + (u - t1) / (t2 - t1) * b2;
        }

        // ── rendering ─────────────────────────────────────────────────────────

        private void ApplyPath(Vector3[] pts)
        {
            _smoothedPath = pts;
            PrecomputeLengths();

            EnsureLineRenderer();
            _line.positionCount = pts.Length;
            _line.SetPositions(pts);
            _line.enabled = _pathVisible;

            _arrowPhase = 0f;
            SetArrowsVisible(_pathVisible);
        }

        private void PrecomputeLengths()
        {
            if (_smoothedPath == null || _smoothedPath.Length < 2)
            {
                _segLengths  = null;
                _totalLength = 0f;
                return;
            }
            _segLengths  = new float[_smoothedPath.Length - 1];
            _totalLength = 0f;
            for (int i = 0; i < _segLengths.Length; i++)
            {
                _segLengths[i] = Vector3.Distance(_smoothedPath[i], _smoothedPath[i + 1]);
                _totalLength  += _segLengths[i];
            }
        }

        private void AnimateArrows()
        {
            float fractionPerSec = ArrowSpeedMps / _totalLength;
            _arrowPhase = (_arrowPhase + Time.deltaTime * fractionPerSec) % 1f;
            for (int a = 0; a < ArrowCount; a++)
            {
                float t = (_arrowPhase + (float)a / ArrowCount) % 1f;
                SamplePolyline(t, out Vector3 pos, out Vector3 tangent);
                DrawChevron(_arrows[a], pos, tangent);
            }
        }

        private void SamplePolyline(float t, out Vector3 pos, out Vector3 tangent)
        {
            float targetDist = t * _totalLength;
            float walked = 0f;
            for (int i = 0; i < _segLengths.Length; i++)
            {
                float segLen = _segLengths[i];
                if (walked + segLen >= targetDist || i == _segLengths.Length - 1)
                {
                    float local = Mathf.Clamp01((targetDist - walked) / Mathf.Max(segLen, 0.001f));
                    pos     = Vector3.Lerp(_smoothedPath[i], _smoothedPath[i + 1], local);
                    tangent = (_smoothedPath[i + 1] - _smoothedPath[i]).normalized;
                    return;
                }
                walked += segLen;
            }
            pos     = _smoothedPath[_smoothedPath.Length - 1];
            tangent = Vector3.forward;
        }

        private void DrawChevron(LineRenderer lr, Vector3 tip, Vector3 forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            if (right.sqrMagnitude < 0.001f) right = Vector3.right;

            float rad  = ArrowAngle * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);
            Vector3 back = -forward;

            Vector3 arm1 = tip + (back * cosA + right * sinA) * ArrowSize;
            Vector3 arm2 = tip + (back * cosA - right * sinA) * ArrowSize;

            lr.positionCount = 3;
            lr.SetPosition(0, arm1);
            lr.SetPosition(1, tip);
            lr.SetPosition(2, arm2);
        }

        private void ClearPath()
        {
            _smoothedPath = null;
            _segLengths   = null;
            _totalLength  = 0f;
            if (_line != null) _line.positionCount = 0;
            HideArrows();
        }

        // ── factories ─────────────────────────────────────────────────────────

        private void EnsureLineRenderer()
        {
            if (_line != null) return;
            var go = new GameObject("CapsulePathRenderer");
            DontDestroyOnLoad(go);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace         = true;
            _line.loop                  = false;
            _line.widthMultiplier       = 0.09f;
            _line.positionCount         = 0;
            _line.numCapVertices        = 4;
            _line.numCornerVertices     = 8;
            _line.shadowCastingMode     = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows        = false;
            _line.material              = BuildLineMaterial(PathColor);
            _line.startColor            = PathColor;
            _line.endColor              = PathColor;
            _line.textureMode           = LineTextureMode.Stretch;
            _line.alignment             = LineAlignment.View;
            _line.enabled               = _pathVisible;
        }

        private void EnsureArrows()
        {
            if (_arrows.Count >= ArrowCount) return;

            Material arrowMaterial = BuildLineMaterial(ArrowColor);
            for (int i = _arrows.Count; i < ArrowCount; i++)
            {
                var go = new GameObject($"CapsuleArrow_{i}");
                DontDestroyOnLoad(go);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace     = true;
                lr.positionCount     = 3;
                lr.widthMultiplier   = 0.22f;
                lr.numCapVertices    = 2;
                lr.numCornerVertices = 4;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows    = false;
                lr.sharedMaterial    = arrowMaterial;
                lr.startColor        = ArrowColor;
                lr.endColor          = ArrowColor;
                lr.alignment         = LineAlignment.View;
                lr.enabled           = false;
                _arrows.Add(lr);
            }
        }

        private void SetArrowsVisible(bool visible)
        {
            bool show = visible && _smoothedPath != null && _totalLength > MinAnimatedPathLength;
            foreach (var lr in _arrows)
                if (lr != null) lr.enabled = show;
        }

        private void HideArrows()
        {
            foreach (var lr in _arrows) if (lr != null) lr.enabled = false;
        }

        private Material BuildLineMaterial(Color color)
        {
            // Sprites/Default first: it alpha-blends and samples vertex colors, so
            // translucent lines and start/endColor work. A runtime-created
            // "Universal Render Pipeline/Unlit" material would be opaque (that
            // shader only turns transparent via editor-side setup) and ignores
            // vertex color, so it is only a last resort.
            Shader shader =
                Shader.Find("Sprites/Default")
                ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Logger.LogError("[CapsulePath] No usable shader found; path will not render.");
                return null;
            }
            return new Material(shader) { color = color };
        }

        // ── on-screen status marker ────────────────────────────────────────────

        private void ShowStatus(string text)
        {
            _statusText    = text;
            _statusShownAt = Time.unscaledTime;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_statusText) || !ShowStatusHud) return;

            float age = Time.unscaledTime - _statusShownAt;
            if (age < 0f || age > StatusDuration) return;
            float alpha = age > StatusDuration - StatusFade
                ? Mathf.Clamp01((StatusDuration - age) / StatusFade)
                : 1f;

            EnsureStatusStyle();
            float pad  = Mathf.Round(Screen.height * 0.012f);
            float dot  = _statusStyle.fontSize;
            Vector2 ts = _statusStyle.CalcSize(new GUIContent(_statusText));
            float boxW = ts.x + dot + pad * 3f;
            float boxH = Mathf.Max(ts.y, dot) + pad * 2f;
            float x    = Mathf.Round(Screen.width * 0.03f);
            float y    = Mathf.Round(Screen.height * 0.5f - boxH * 0.5f);

            Color prev = GUI.color;

            // Panel
            GUI.color = new Color(0f, 0f, 0f, 0.66f * alpha);
            GUI.DrawTexture(new Rect(x, y, boxW, boxH), _statusTex);

            // Activity marker: green while a path is drawn and visible, grey otherwise.
            GUI.color = (_smoothedPath != null && _pathVisible)
                ? new Color(0.2f, 1f, 0.3f, alpha)
                : new Color(0.6f, 0.6f, 0.6f, alpha);
            GUI.DrawTexture(new Rect(x + pad, y + (boxH - dot) * 0.5f, dot, dot), _statusTex);

            // Text
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x + pad * 2f + dot, y, ts.x + pad, boxH), _statusText, _statusStyle);

            GUI.color = prev;
        }

        private void EnsureStatusStyle()
        {
            if (_statusTex == null)
            {
                _statusTex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _statusTex.SetPixel(0, 0, Color.white);
                _statusTex.Apply();
            }
            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = FontStyle.Bold,
                    richText  = false,
                };
                _statusStyle.normal.textColor = Color.white;
            }
            _statusStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.022f), 12, 40);
        }

        // ── player position helpers ────────────────────────────────────────────

        private static bool TryGetLocalAvatarPosition(Player local, out Vector3 position, out string source)
        {
            try
            {
                Vector3 head = local.HeadPosition();
                if (Physics.Raycast(head, Vector3.down, out RaycastHit hit, 3f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    position = hit.point + Vector3.up * 0.1f;
                    source   = "HeadPosition+groundRay";
                }
                else
                {
                    position = head + Vector3.down * 1.5f;
                    source   = "HeadPosition-1.5m";
                }
                return true;
            }
            catch
            {
                // HeadPosition() can throw while the avatar is spawning or being torn down.
            }

            if (local != null) // Unity's null-check also covers destroyed objects
            {
                position = local.transform.position;
                source   = "transform.position";
                return true;
            }

            position = default;
            source   = "none";
            return false;
        }

        private static bool TrySampleToNavMesh(ref Vector3 position)
        {
            if (!NavMesh.SamplePosition(position, out var hit, 4f, NavMesh.AllAreas))
                return false;
            position = hit.position;
            return true;
        }
    }
}
