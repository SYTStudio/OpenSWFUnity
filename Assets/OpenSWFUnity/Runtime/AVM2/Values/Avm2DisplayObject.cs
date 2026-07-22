using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // A node in the ActionScript 3 display tree.
    //
    // The AS3 instance and the record the renderer walks are the same object, so
    // reaching a display object from script never needs a wrapper lookup or a side
    // table to stay in sync. Transform components are stored the way AS3 exposes
    // them (position, scale, rotation) and composed into a matrix at draw time,
    // which keeps a script assignment to `x` a single field write.
    public class Avm2DisplayObject : Avm2EventDispatcher
    {
        public double X;
        public double Y;
        public double ScaleX = 1d;
        public double ScaleY = 1d;
        public double Rotation;
        public double Alpha = 1d;
        public bool Visible = true;
        public string Name = string.Empty;

        public Avm2DisplayObject Parent;

        // Allocated only for containers that actually receive children.
        private List<Avm2DisplayObject> children;

        // The SWF character this object draws, or 0 for a purely structural node
        // such as a plain Sprite used as a group.
        public ushort CharacterId;

        // MovieClip playhead. Frame numbers are 1-based, matching AS3.
        public int CurrentFrame = 1;
        public int TotalFrames = 1;
        public bool IsPlaying = true;

        // Set on the two singletons the runtime creates, so `stage` and `root` can be
        // answered by walking up rather than by holding back-references on every node.
        public bool IsStage;
        public bool IsRoot;

        public Avm2DisplayObject()
        {
        }

        public Avm2DisplayObject(Avm2Class type) : base(type)
        {
        }

        public int NumChildren => children != null ? children.Count : 0;

        public IReadOnlyList<Avm2DisplayObject> Children =>
            children ?? (IReadOnlyList<Avm2DisplayObject>)System.Array.Empty<Avm2DisplayObject>();

        public Avm2DisplayObject GetChildAt(int index)
        {
            return children != null && index >= 0 && index < children.Count
                ? children[index]
                : null;
        }

        public int GetChildIndex(Avm2DisplayObject child)
        {
            return children != null ? children.IndexOf(child) : -1;
        }

        public Avm2DisplayObject GetChildByName(string name)
        {
            if (children == null || string.IsNullOrEmpty(name))
                return null;

            for (int i = 0; i < children.Count; i++)
            {
                if (string.Equals(children[i].Name, name, System.StringComparison.Ordinal))
                    return children[i];
            }

            return null;
        }

        // Adds at `index`, or appends when index is negative. Re-parenting is handled
        // by detaching first, which is what AS3 does when a child is added to a new
        // container while still in an old one.
        public void AddChild(Avm2DisplayObject child, int index = -1)
        {
            if (child == null || ReferenceEquals(child, this))
                return;

            child.Parent?.RemoveChild(child);
            children ??= new List<Avm2DisplayObject>();

            if (index < 0 || index >= children.Count)
                children.Add(child);
            else
                children.Insert(index, child);

            child.Parent = this;
        }

        public bool RemoveChild(Avm2DisplayObject child)
        {
            if (children == null || child == null)
                return false;

            if (!children.Remove(child))
                return false;

            child.Parent = null;
            return true;
        }

        public Avm2DisplayObject RemoveChildAt(int index)
        {
            if (children == null || index < 0 || index >= children.Count)
                return null;

            Avm2DisplayObject child = children[index];
            children.RemoveAt(index);
            child.Parent = null;
            return child;
        }

        // True when `candidate` is this object or anywhere beneath it. Guarded against
        // a cycle that a malformed script could otherwise create.
        public bool Contains(Avm2DisplayObject candidate)
        {
            int guard = 0;

            while (candidate != null && guard++ < 1024)
            {
                if (ReferenceEquals(candidate, this))
                    return true;

                candidate = candidate.Parent;
            }

            return false;
        }

        public Avm2DisplayObject FindStage()
        {
            Avm2DisplayObject current = this;
            int guard = 0;

            while (current != null && guard++ < 1024)
            {
                if (current.IsStage)
                    return current;

                current = current.Parent;
            }

            return null;
        }

        public Avm2DisplayObject FindRoot()
        {
            Avm2DisplayObject current = this;
            Avm2DisplayObject lastBeforeStage = null;
            int guard = 0;

            while (current != null && guard++ < 1024)
            {
                if (current.IsRoot)
                    return current;

                if (!current.IsStage)
                    lastBeforeStage = current;

                current = current.Parent;
            }

            // An object not yet on the stage reports the topmost ancestor it has,
            // which matches how AS3 answers `root` for a detached subtree.
            return lastBeforeStage;
        }

        public bool IsOnStage => FindStage() != null;

        // Path from the stage down to this object, used to drive the capture and
        // bubble phases of event dispatch.
        public void BuildPropagationPath(List<Avm2DisplayObject> path)
        {
            path.Clear();
            Avm2DisplayObject current = Parent;
            int guard = 0;

            while (current != null && guard++ < 1024)
            {
                path.Add(current);
                current = current.Parent;
            }

            path.Reverse();
        }

        public override string ToString()
        {
            string typeName = Class != null ? Class.Name.Local : "DisplayObject";
            return string.IsNullOrEmpty(Name)
                ? "[object " + typeName + "]"
                : "[object " + typeName + " " + Name + "]";
        }
    }
}
