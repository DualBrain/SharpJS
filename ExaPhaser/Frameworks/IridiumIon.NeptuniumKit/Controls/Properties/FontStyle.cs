using IridiumIon.NeptuniumKit.ComponentModel;

namespace IridiumIon.NeptuniumKit.Controls.Properties
{
    public class FontStyle : IRaisePropertyChanged
    {
        private int _textSize = 12;
        private FontWeight _weight;

        /// <summary>
        /// The size of the text
        /// </summary>
        public int TextSize { get { return _textSize; } set { _textSize = value; OnPropertyChanged(nameof(TextSize)); } }

        public FontWeight Weight { get { return _weight; } set { _weight = value; OnPropertyChanged(nameof(Weight)); } }

        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}