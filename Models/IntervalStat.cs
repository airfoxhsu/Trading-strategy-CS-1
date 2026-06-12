using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExtremeSignalAppCS.Models
{
    public class IntervalStat : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private string _intervalName = "";
        private int _shortCount;
        private int _longCount;
        private string _displayText = "";
        private string _displayColor = "#DCDCDC";

        public string IntervalName { get => _intervalName; set => SetField(ref _intervalName, value); }
        public int ShortCount { get => _shortCount; set => SetField(ref _shortCount, value); }
        public int LongCount { get => _longCount; set => SetField(ref _longCount, value); }
        
        public string DisplayText { get => _displayText; set => SetField(ref _displayText, value); }
        public string DisplayColor { get => _displayColor; set => SetField(ref _displayColor, value); }
    }
}
