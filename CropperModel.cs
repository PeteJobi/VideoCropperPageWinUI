using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using WinUIShared.Enums;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VideoCropper
{
    public class CropperModel: INotifyPropertyChanged
    {
        private bool _isplaying;
        public bool IsPlaying
        {
            get => _isplaying;
            set
            {
                _isplaying = value;
                OnPropertyChanged();
            }
        }
        private OperationState _state;
        public OperationState State
        {
            get => _state;
            set
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BeforeOperation));
                OnPropertyChanged(nameof(DuringOperation));
                OnPropertyChanged(nameof(AfterOperation));
            }
        }

        public bool BeforeOperation => State == OperationState.BeforeOperation;
        public bool DuringOperation => State == OperationState.DuringOperation;
        public bool AfterOperation => State == OperationState.AfterOperation;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
