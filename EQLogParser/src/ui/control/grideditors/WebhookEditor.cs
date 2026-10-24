using FontAwesome5;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal sealed class WebhookEditor(WebhookDispatchQueue queue, Func<(string Character, string Trigger)> selection)
    : BaseTypeEditor
  {
    private Grid _grid;
    private TextBox _text;
    private ImageAwesome _error;

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();
    public override bool ShouldPropertyGridTryToHandleKeyDown(Key _) => false;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      BindingOperations.SetBinding(_text, TextBox.TextProperty, new Binding("Value")
      {
        Source = info,
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      });
      _text.IsReadOnly = !info.CanWrite;
      queue.EventsStatusChanged -= Refresh;
      queue.EventsStatusChanged += Refresh;
      Refresh();
    }

    public override void Detach(PropertyViewItem property)
    {
      queue.EventsStatusChanged -= Refresh;
      if (_text is not null)
      {
        _text.TextChanged -= TextChanged;
        BindingOperations.ClearAllBindings(_text);
      }

      _grid = null;
      _text = null;
      _error = null;
    }

    internal void FocusWebhook()
    {
      _text?.BringIntoView();
      _text?.Focus();
    }

    private object Create()
    {
      if (_grid is not null) return _grid;
      _grid = new Grid();
      _grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
      _grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
      _text = new TextBox
      {
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0, 2, 0, 2),
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0)
      };
      _text.TextChanged += TextChanged;
      _error = new ImageAwesome
      {
        Icon = EFontAwesomeIcon.Solid_ExclamationTriangle,
        Margin = new Thickness(6, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
      };
      _error.SetResourceReference(ImageAwesome.ForegroundProperty, "EQStopForegroundBrush");
      _error.SetResourceReference(FrameworkElement.HeightProperty, "EQContentSize");
      _error.SetResourceReference(FrameworkElement.WidthProperty, "EQContentSize");
      Grid.SetColumn(_error, 1);
      _grid.Children.Add(_text);
      _grid.Children.Add(_error);
      return _grid;
    }

    private void TextChanged(object sender, TextChangedEventArgs args) => Refresh();

    private void Refresh()
    {
      _text?.Dispatcher.InvokeAsync(() =>
      {
        if (_text is null || _error is null) return;
        var (character, trigger) = selection();
        var message = queue.ErrorFor(_text.Text, character, trigger);
        _error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        _error.ToolTip = message;
        _text.ToolTip = message;
        _text.SetResourceReference(Control.ForegroundProperty, message == null ? "ContentForeground" : "EQStopForegroundBrush");
      });
    }
  }
}