using System;
using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Script
{
    public interface ISwfScriptRuntime
    {
        object RootObject { get; }
        bool VerboseLogging { get; set; }
        Func<string, IReadOnlyList<object>, object> ExternalFunction { get; set; }
        Func<object, string, IReadOnlyList<object>, object> ExternalMethod { get; set; }
        Action<string> Trace { get; set; }
        Action<string> Warning { get; set; }
        int DefinedFunctionCount { get; }

        object CreateObject();
        bool ApplyRegisteredClass(string linkageName, object instance);
        void RegisterFunctions(byte[] actionBytes);
        object Execute(byte[] actionBytes, object thisObject);
        bool TryCallFunction(string functionName, IReadOnlyList<object> arguments, out object result);
        bool TryCallMethod(object receiver, string methodName, IReadOnlyList<object> arguments, out object result);
        object GetVariable(string name);
        void SetVariable(string name, object value);
    }
}
