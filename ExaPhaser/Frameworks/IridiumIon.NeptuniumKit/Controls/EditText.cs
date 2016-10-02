namespace IridiumIon.NeptuniumKit.Controls
{
    /// <summary>
    /// TODO!
    /// </summary>
    public class EditText : TextView
    {
        /// <summary>
        /// A placeholder to be shown when the text is empty.
        /// </summary>
        public string Placeholder
        {
            get
            {
                return UnderlyingJQElement.Attr("placeholder");
            }
            set
            {
                UnderlyingJQElement.Attr("placeholder", value);
            }
        }
    }
}