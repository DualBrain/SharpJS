﻿using System;

namespace IridiumIon.NeptuniumKit.ComponentModel
{
    public interface ICommand
    {
        void Execute(object parameter);

        bool CanExecute(object parameter);

        event EventHandler CanExecuteChanged;
    }
}