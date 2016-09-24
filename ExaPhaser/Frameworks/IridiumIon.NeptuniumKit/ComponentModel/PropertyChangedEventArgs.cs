using System;

namespace IridiumIon.NeptuniumKit.ComponentModel
{
    public class PropertyChangedEventArgs : EventArgs
    {
        public PropertyChangedEventArgs(string propertyName)
        {
            PropertyName = propertyName;
        }

        public virtual string PropertyName { get; }
    }
}