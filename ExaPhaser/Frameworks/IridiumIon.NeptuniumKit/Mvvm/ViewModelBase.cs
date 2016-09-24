using IridiumIon.NeptuniumKit.ComponentModel;

namespace IridiumIon.NeptuniumKit.Mvvm
{
    /// <summary>
	/// A base class for NeptuniumKit ViewModels
	/// </summary>
	public class ViewModelBase : IRaisePropertyChanged, INotifyPropertyChanged
    {
        #region INotifyPropertyChanged Members

        /// <summary>
        ///     Raised when a property on this object has a new value.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        ///     Raises this object's PropertyChanged event.
        /// </summary>
        /// <param name="propertyName">The property that has a new value.</param>
        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion INotifyPropertyChanged Members
    }
}