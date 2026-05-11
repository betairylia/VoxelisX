using System;

namespace Voxelis
{
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class InspectorButtonAttribute : Attribute
    {
        public InspectorButtonAttribute(string label = null)
        {
            Label = label;
        }

        public string Label { get; }
        public bool PlayModeOnly { get; set; }
        public bool EditModeOnly { get; set; }
    }
}
