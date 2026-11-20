using System.Runtime.Serialization;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// Persisted size/position of the keeper's own main window.  Like the
    /// Explorer and browser windows, the keeper reopens at the size the user
    /// last left it instead of forcing a fixed default.
    /// </summary>
    [DataContract]
    public sealed class MainWindowState
    {
        public MainWindowState()
        {
            Width = 1080;
            Height = 720;
        }

        [DataMember(Order = 1)]
        public double Width { get; set; }

        [DataMember(Order = 2)]
        public double Height { get; set; }

        [DataMember(Order = 3)]
        public double Left { get; set; }

        [DataMember(Order = 4)]
        public double Top { get; set; }

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public bool HasPosition { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public bool Maximized { get; set; }
    }
}
