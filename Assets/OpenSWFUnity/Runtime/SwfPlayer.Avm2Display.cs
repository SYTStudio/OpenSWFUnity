using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using OpenSWFUnity.Runtime.AVM2;
using OpenSWFUnity.Runtime.AVM2.Values;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Renderer;

namespace OpenSWFUnity.Runtime
{
    // Binds the ActionScript 3 display tree to the player: renders it, feeds it frame
    // and input events, and answers the questions the flash.display classes ask about
    // stage size, character bounds and the pointer.
    //
    // Everything here is inert unless the movie is AS3 (runtimeAvm2 non-null), so
    // AVM1 playback is untouched.
    public partial class SwfPlayer : IAvm2DisplayHost
    {
        private readonly List<Avm2DisplayObject> avm2RenderBuffer = new List<Avm2DisplayObject>();
        private Avm2DisplayObject avm2HoverTarget;
        private Avm2DisplayObject avm2PressTarget;
        private Vector2 avm2StagePoint;
        private bool avm2TextRenderingReported;

        // World matrix per display object, rebuilt once per frame during the hit-test
        // walk. Local coordinates and per-object mouseX/mouseY then cost a cached
        // lookup plus one inverse, instead of re-deriving the chain per query.
        private readonly Dictionary<Avm2DisplayObject, SwfMatrix> avm2WorldMatrices =
            new Dictionary<Avm2DisplayObject, SwfMatrix>();

        // Double click is reported when two clicks land on the same object within
        // this window, which is the interval Flash uses.
        private const float DoubleClickSeconds = 0.4f;
        private Avm2DisplayObject avm2LastClickTarget;
        private float avm2LastClickTime = float.NegativeInfinity;

        // ---- setup ------------------------------------------------------------

        private void InitializeAvm2Display(SwfParser parser)
        {
            if (runtimeAvm2 == null || parser == null)
                return;

            runtimeAvm2.Builtins.DisplayHost = this;

            int rootFrames = parser.RootFrames != null && parser.RootFrames.Count > 0
                ? parser.RootFrames.Count
                : Mathf.Max(1, parser.Header != null ? parser.Header.FrameCount : 1);

            runtimeAvm2.CreateStage(rootFrames);

            string documentClass = FindDocumentClassName(parser);

            if (string.IsNullOrEmpty(documentClass))
                return;

            if (runtimeAvm2.TryConstructDocumentClass(documentClass, rootFrames) && verboseLogging)
                Debug.Log("AS3 document class '" + documentClass + "' constructed as the root.");
        }

        // SymbolClass binds character 0 - the main timeline - to the document class.
        // The parser records those bindings alongside ExportAssets, keyed by name.
        private string FindDocumentClassName(SwfParser parser)
        {
            if (parser.ExportedAssets == null)
                return null;

            foreach (KeyValuePair<string, ushort> asset in parser.ExportedAssets)
            {
                if (asset.Value == 0)
                    return asset.Key;
            }

            return null;
        }

        // ---- per-frame --------------------------------------------------------

        private void AdvanceAvm2Frame()
        {
            if (runtimeAvm2 == null || runtimeAvm2.Stage == null)
                return;

            // The root's playhead follows the main timeline unless script stopped it.
            Avm2DisplayObject root = runtimeAvm2.Root;

            if (root != null && root.IsPlaying && runtimeParser?.RootFrames != null &&
                runtimeParser.RootFrames.Count > 0)
            {
                root.CurrentFrame = (currentTimelineFrame % runtimeParser.RootFrames.Count) + 1;
            }

            runtimeAvm2.BroadcastFrameEvent("enterFrame");
        }

        // ---- rendering --------------------------------------------------------

        private void RenderAvm2DisplayTree(SwfParser parser, SwfDebugRenderer renderer)
        {
            if (runtimeAvm2 == null || runtimeAvm2.Stage == null)
                return;

            RenderAvm2Node(parser, renderer, runtimeAvm2.Stage, SwfMatrix.Identity, 1f, 0);
        }

        private void RenderAvm2Node(
            SwfParser parser,
            SwfDebugRenderer renderer,
            Avm2DisplayObject node,
            SwfMatrix parentMatrix,
            float parentAlpha,
            int depth
        )
        {
            if (node == null || depth > 64 || !node.Visible)
                return;

            float alpha = parentAlpha * Mathf.Clamp01((float)node.Alpha);

            if (alpha <= 0.001f)
                return;

            SwfMatrix world = SwfMatrix.Combine(parentMatrix, BuildAvm2Matrix(node));

            if (node.CharacterId != 0)
            {
                RenderCharacterDebug(
                    parser,
                    renderer,
                    node.CharacterId,
                    world,
                    "AS3_" + (string.IsNullOrEmpty(node.Name) ? "obj" : node.Name),
                    alpha,
                    Mathf.Max(0, node.CurrentFrame - 1),
                    0,
                    false,
                    null,
                    "AS3/" + node.Name,
                    false,
                    true
                );
            }

            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = 0; i < children.Count; i++)
                RenderAvm2Node(parser, renderer, children[i], world, alpha, depth + 1);
        }

        // AS3 exposes position, scale and rotation separately; the renderer needs the
        // composed affine matrix.
        private static SwfMatrix BuildAvm2Matrix(Avm2DisplayObject node)
        {
            float rotation = (float)node.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rotation);
            float sin = Mathf.Sin(rotation);
            float scaleX = (float)node.ScaleX;
            float scaleY = (float)node.ScaleY;

            return new SwfMatrix
            {
                ScaleX = cos * scaleX,
                RotateSkew0 = sin * scaleX,
                RotateSkew1 = -sin * scaleY,
                ScaleY = cos * scaleY,
                TranslateX = (float)node.X,
                TranslateY = (float)node.Y
            };
        }

        // ---- input ------------------------------------------------------------

        // Sampled once per input pass and stored on the runtime, so every event
        // created from this pass reports the same modifier state.
        private void CaptureModifierState()
        {
            if (runtimeAvm2 == null)
                return;

            Keyboard keyboard = Keyboard.current;

            if (keyboard == null)
            {
                runtimeAvm2.ShiftDown = false;
                runtimeAvm2.ControlDown = false;
                runtimeAvm2.AltDown = false;
                return;
            }

            runtimeAvm2.ShiftDown =
                keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            runtimeAvm2.ControlDown =
                keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            runtimeAvm2.AltDown =
                keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
        }

        // Builds and dispatches a mouse event with the target's own local coordinates.
        // rollOver/rollOut are the two that do not bubble in Flash.
        private void DispatchMouse(
            Avm2DisplayObject target,
            string type,
            Vector2 stagePoint,
            bool buttonDown,
            bool bubbles = true
        )
        {
            if (runtimeAvm2 == null || target == null)
                return;

            Vector2 local = StageToLocal(target, stagePoint);
            Avm2MouseEventObject e = runtimeAvm2.CreateMouseEvent(
                type, stagePoint.x, stagePoint.y, buttonDown, local.x, local.y);
            e.Bubbles = bubbles;
            runtimeAvm2.DispatchEvent(target, e);
        }

        // Stage space to the target's own space, through the world matrix recorded
        // during the last hit-test walk. A singular matrix - a zero scale - has no
        // inverse, so the stage point is returned unchanged rather than NaN.
        private Vector2 StageToLocal(Avm2DisplayObject target, Vector2 stagePoint)
        {
            if (target == null)
                return stagePoint;

            if (!avm2WorldMatrices.TryGetValue(target, out SwfMatrix world))
            {
                // Not visited this frame (an object off the hit path, or a query made
                // before any pointer movement); derive it from the parent chain once.
                world = ComputeAvm2WorldMatrix(target, 0);
                avm2WorldMatrices[target] = world;
            }

            return world.TryInvert(out SwfMatrix inverse)
                ? inverse.TransformPoint(stagePoint)
                : stagePoint;
        }

        private static SwfMatrix ComputeAvm2WorldMatrix(Avm2DisplayObject node, int depth)
        {
            if (node == null || depth > 64)
                return SwfMatrix.Identity;

            SwfMatrix local = BuildAvm2Matrix(node);

            return node.Parent == null
                ? local
                : SwfMatrix.Combine(ComputeAvm2WorldMatrix(node.Parent, depth + 1), local);
        }

        private void DispatchAvm2PointerEvents(Vector2 flashPoint)
        {
            if (runtimeAvm2 == null || runtimeAvm2.Stage == null)
                return;

            avm2StagePoint = flashPoint;
            CaptureModifierState();
            bool buttonDown = Mouse.current != null && Mouse.current.leftButton.isPressed;
            Avm2DisplayObject hit = FindAvm2ObjectAt(flashPoint) ?? runtimeAvm2.Root;

            if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 0.0001f)
                DispatchMouse(hit, "mouseMove", flashPoint, buttonDown);

            // rollOver/rollOut do not bubble in Flash; mouseOver/mouseOut do.
            if (!ReferenceEquals(hit, avm2HoverTarget))
            {
                if (avm2HoverTarget != null)
                {
                    DispatchMouse(avm2HoverTarget, "mouseOut", flashPoint, buttonDown);
                    DispatchMouse(avm2HoverTarget, "rollOut", flashPoint, buttonDown, false);
                }

                if (hit != null)
                {
                    DispatchMouse(hit, "mouseOver", flashPoint, buttonDown);
                    DispatchMouse(hit, "rollOver", flashPoint, buttonDown, false);
                }

                avm2HoverTarget = hit;
            }

            if (Mouse.current == null)
                return;

            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                avm2PressTarget = hit;

                // Pressing an object gives it keyboard focus, which is how a movie
                // routes keys to whatever was last interacted with.
                runtimeAvm2.FocusObject = hit;
                DispatchMouse(hit, "mouseDown", flashPoint, true);
            }
            else if (Mouse.current.leftButton.wasReleasedThisFrame)
            {
                DispatchMouse(hit, "mouseUp", flashPoint, false);

                // A click only counts when press and release landed on the same object.
                if (ReferenceEquals(hit, avm2PressTarget) && hit != null)
                {
                    DispatchMouse(hit, "click", flashPoint, false);

                    bool repeat =
                        ReferenceEquals(hit, avm2LastClickTarget) &&
                        Time.unscaledTime - avm2LastClickTime <= DoubleClickSeconds;

                    if (repeat)
                    {
                        DispatchMouse(hit, "doubleClick", flashPoint, false);

                        // Cleared so a third click starts a new pair rather than
                        // firing doubleClick again.
                        avm2LastClickTarget = null;
                        avm2LastClickTime = float.NegativeInfinity;
                    }
                    else
                    {
                        avm2LastClickTarget = hit;
                        avm2LastClickTime = Time.unscaledTime;
                    }
                }

                avm2PressTarget = null;
            }
        }

        // Used when the movie is AS3 only, so there is no AVM1 key loop to piggyback
        // on. Mirrors the same key-code translation the AVM1 path uses.
        private void DispatchAvm2KeyboardInput()
        {
            if (runtimeAvm2 == null || Keyboard.current == null)
                return;

            var keys = Keyboard.current.allKeys;

            for (int i = 0; i < keys.Count; i++)
            {
                var control = keys[i];

                if (!control.wasPressedThisFrame && !control.wasReleasedThisFrame)
                    continue;

                int flashCode = ToFlashKeyCode(control.keyCode);

                if (flashCode == 0)
                    continue;

                DispatchAvm2KeyEvent(
                    control.wasPressedThisFrame,
                    flashCode,
                    ToFlashAsciiCode(control.keyCode, flashCode));
            }
        }

        private void DispatchAvm2KeyEvent(bool pressed, int flashKeyCode, int asciiCode)
        {
            if (runtimeAvm2 == null || runtimeAvm2.Stage == null)
                return;

            CaptureModifierState();

            // Keys go to whatever holds focus and bubble from there; with nothing
            // focused that resolves to the stage.
            runtimeAvm2.DispatchEvent(
                runtimeAvm2.ResolveKeyboardTarget(),
                runtimeAvm2.CreateKeyboardEvent(pressed ? "keyDown" : "keyUp", flashKeyCode, asciiCode));
        }

        // Topmost-first search: children draw over their parents, so the last drawn
        // node that contains the point is the one the pointer is over.
        private Avm2DisplayObject FindAvm2ObjectAt(Vector2 flashPoint)
        {
            if (runtimeAvm2?.Stage == null)
                return null;

            // One walk per frame populates the transform cache that local coordinates
            // and mouseX/mouseY then read, instead of each query re-deriving it.
            avm2WorldMatrices.Clear();
            return HitTestAvm2Node(runtimeAvm2.Stage, SwfMatrix.Identity, flashPoint, 0);
        }

        private Avm2DisplayObject HitTestAvm2Node(
            Avm2DisplayObject node,
            SwfMatrix parentMatrix,
            Vector2 point,
            int depth
        )
        {
            // Invisible and fully transparent subtrees are not hit targets in Flash,
            // and skipping them prunes the walk as well.
            if (node == null || depth > 64 || !node.Visible || node.Alpha <= 0.001d)
                return null;

            SwfMatrix world = SwfMatrix.Combine(parentMatrix, BuildAvm2Matrix(node));
            avm2WorldMatrices[node] = world;
            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = children.Count - 1; i >= 0; i--)
            {
                Avm2DisplayObject hit = HitTestAvm2Node(children[i], world, point, depth + 1);

                if (hit != null)
                    return hit;
            }

            if (node.CharacterId == 0 || node.IsStage)
                return null;

            return ContainsPoint(node.CharacterId, world, point) ? node : null;
        }

        private bool ContainsPoint(ushort characterId, SwfMatrix world, Vector2 point)
        {
            if (!TryGetCharacterBounds(characterId, out Rect bounds))
                return false;

            // Corners are transformed rather than the point inverse-transformed, so a
            // singular matrix simply yields an empty box instead of failing.
            Vector2 c0 = world.TransformPoint(bounds.xMin, bounds.yMin);
            Vector2 c1 = world.TransformPoint(bounds.xMax, bounds.yMin);
            Vector2 c2 = world.TransformPoint(bounds.xMax, bounds.yMax);
            Vector2 c3 = world.TransformPoint(bounds.xMin, bounds.yMax);

            float minX = Mathf.Min(Mathf.Min(c0.x, c1.x), Mathf.Min(c2.x, c3.x));
            float maxX = Mathf.Max(Mathf.Max(c0.x, c1.x), Mathf.Max(c2.x, c3.x));
            float minY = Mathf.Min(Mathf.Min(c0.y, c1.y), Mathf.Min(c2.y, c3.y));
            float maxY = Mathf.Max(Mathf.Max(c0.y, c1.y), Mathf.Max(c2.y, c3.y));

            return point.x >= minX && point.x <= maxX && point.y >= minY && point.y <= maxY;
        }

        // ---- IAvm2DisplayHost --------------------------------------------------

        double IAvm2DisplayHost.StageWidth =>
            runtimeParser?.Header != null ? runtimeParser.Header.StageWidth : 0d;

        double IAvm2DisplayHost.StageHeight =>
            runtimeParser?.Header != null ? runtimeParser.Header.StageHeight : 0d;

        double IAvm2DisplayHost.FrameRate => swfFrameRate;

        string IAvm2DisplayHost.Quality => renderQuality.ToString().ToUpperInvariant();

        bool IAvm2DisplayHost.DispatchEvent(object target, Avm2EventObject e)
        {
            return runtimeAvm2 != null && runtimeAvm2.DispatchEvent(target, e);
        }

        double IAvm2DisplayHost.GetWidth(Avm2DisplayObject target)
        {
            return MeasureAvm2(target, true);
        }

        double IAvm2DisplayHost.GetHeight(Avm2DisplayObject target)
        {
            return MeasureAvm2(target, false);
        }

        // Measured in the parent's space: the object's own extent times its own
        // scale, matching how Flash reports width and height.
        private double MeasureAvm2(Avm2DisplayObject target, bool horizontal)
        {
            if (target == null || !TryGetAvm2LocalBounds(target, out Rect bounds, 0))
                return 0d;

            return horizontal
                ? bounds.width * Mathf.Abs((float)target.ScaleX)
                : bounds.height * Mathf.Abs((float)target.ScaleY);
        }

        // Local bounds of a node: its own character, unioned with its children's
        // bounds after each child's transform.
        private bool TryGetAvm2LocalBounds(Avm2DisplayObject node, out Rect bounds, int depth)
        {
            bounds = new Rect(0f, 0f, -1f, -1f);

            if (node == null || depth > 32)
                return false;

            bool any = false;

            if (node.CharacterId != 0 && TryGetCharacterBounds(node.CharacterId, out Rect own))
            {
                bounds = own;
                any = true;
            }

            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = 0; i < children.Count; i++)
            {
                if (!TryGetAvm2LocalBounds(children[i], out Rect childBounds, depth + 1))
                    continue;

                Rect transformed = TransformBounds(childBounds, BuildAvm2Matrix(children[i]));
                bounds = any ? UnionBounds(bounds, transformed) : transformed;
                any = true;
            }

            return any;
        }

        void IAvm2DisplayHost.SetWidth(Avm2DisplayObject target, double value)
        {
            if (target == null || value < 0d ||
                !TryGetAvm2LocalBounds(target, out Rect bounds, 0) || bounds.width <= 0.0001f)
            {
                return;
            }

            target.ScaleX = value / bounds.width;
        }

        void IAvm2DisplayHost.SetHeight(Avm2DisplayObject target, double value)
        {
            if (target == null || value < 0d ||
                !TryGetAvm2LocalBounds(target, out Rect bounds, 0) || bounds.height <= 0.0001f)
            {
                return;
            }

            target.ScaleY = value / bounds.height;
        }

        double IAvm2DisplayHost.GetMouseX(Avm2DisplayObject target)
        {
            return StageToLocal(target, avm2StagePoint).x;
        }

        double IAvm2DisplayHost.GetMouseY(Avm2DisplayObject target)
        {
            return StageToLocal(target, avm2StagePoint).y;
        }

        int IAvm2DisplayHost.ResolveFrameLabel(Avm2DisplayObject target, string label)
        {
            if (runtimeParser?.RootFrames == null || string.IsNullOrEmpty(label))
                return 1;

            // FrameLabel (tag 43) stores a null-terminated name at the start of its
            // payload; the first frame carrying a matching label wins.
            for (int frame = 0; frame < runtimeParser.RootFrames.Count; frame++)
            {
                List<Tags.SwfTag> tags = runtimeParser.RootFrames[frame].ControlTags;

                for (int i = 0; i < tags.Count; i++)
                {
                    if (tags[i].Code != 43)
                        continue;

                    byte[] data = GetActionBytes(tags[i], 0);
                    int end = 0;

                    while (end < data.Length && data[end] != 0)
                        end++;

                    string name = System.Text.Encoding.UTF8.GetString(data, 0, end);

                    if (string.Equals(name, label, System.StringComparison.Ordinal))
                        return frame + 1;
                }
            }

            return 1;
        }

        object IAvm2DisplayHost.GetFocus()
        {
            return runtimeAvm2 != null ? runtimeAvm2.FocusObject : null;
        }

        void IAvm2DisplayHost.SetFocus(Avm2DisplayObject target)
        {
            if (runtimeAvm2 == null)
                return;

            // Focusing something that is not on the display list would silently
            // swallow keyboard input, so it is treated as clearing focus instead.
            runtimeAvm2.FocusObject = target != null && target.IsOnStage ? target : null;
        }

        void IAvm2DisplayHost.NotifyChildAdded(Avm2DisplayObject child, bool wasOnStage)
        {
            if (runtimeAvm2 == null || child == null)
                return;

            runtimeAvm2.DispatchLifecycleEvent(child, "added", true);

            // addedToStage only fires on the transition onto the stage, not on every
            // re-parent within it.
            if (!wasOnStage && child.IsOnStage)
                DispatchToSubtree(child, "addedToStage");
        }

        void IAvm2DisplayHost.NotifyChildRemoved(Avm2DisplayObject child, bool wasOnStage)
        {
            if (runtimeAvm2 == null || child == null)
                return;

            runtimeAvm2.DispatchLifecycleEvent(child, "removed", true);

            if (wasOnStage)
                DispatchToSubtree(child, "removedFromStage");
        }

        // addedToStage and removedFromStage reach every descendant, since the whole
        // subtree changed its stage membership together.
        private void DispatchToSubtree(Avm2DisplayObject node, string type)
        {
            if (node == null)
                return;

            runtimeAvm2.DispatchLifecycleEvent(node, type, false);
            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = 0; i < children.Count; i++)
                DispatchToSubtree(children[i], type);
        }

        void IAvm2DisplayHost.NotifyFrameChanged(Avm2DisplayObject clip)
        {
            if (clip == null || runtimeAvm2 == null || !ReferenceEquals(clip, runtimeAvm2.Root))
                return;

            // Frame control on the root drives the movie's own playhead.
            currentTimelineFrame = Mathf.Max(0, clip.CurrentFrame - 1);
            autoPlayTimeline = clip.IsPlaying;
            ExecuteCurrentTimelineActions();
            RenderCurrentFrame();
        }

        void IAvm2DisplayHost.NotifyPlayStateChanged(Avm2DisplayObject clip)
        {
            if (clip != null && runtimeAvm2 != null && ReferenceEquals(clip, runtimeAvm2.Root))
                autoPlayTimeline = clip.IsPlaying;
        }

        void IAvm2DisplayHost.NotifyTextChanged(Avm2DisplayObject field)
        {
            if (avm2TextRenderingReported || field == null)
                return;

            avm2TextRenderingReported = true;
            Debug.LogWarning(
                "An ActionScript 3 TextField's text was set. The field's value is stored and " +
                "readable from script, but drawing AS3-authored text is not implemented, so " +
                "nothing appears on screen for it."
            );
        }
    }
}
