using System;

namespace NetworkingLibrary.Modules
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class CustomRPCAttribute : Attribute { }
}
