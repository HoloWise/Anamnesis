﻿// Concept Matrix 3.
// Licensed under the MIT license.

namespace ConceptMatrix.WpfStyles.Controls
{
	using System.ComponentModel;
	using System.Windows;
	using System.Windows.Controls;
	using FontAwesome.WPF;

	/// <summary>
	/// Interaction logic for IconButton.xaml.
	/// </summary>
	public partial class IconButton : UserControl, INotifyPropertyChanged
	{
		public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(IconButton), new FrameworkPropertyMetadata(new PropertyChangedCallback(OnChanged)));
		public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(FontAwesomeIcon), typeof(IconButton), new FrameworkPropertyMetadata(new PropertyChangedCallback(OnChanged)));
		public static readonly RoutedEvent ClickEvent = EventManager.RegisterRoutedEvent(nameof(Click), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(IconButton));

		public IconButton()
		{
			this.InitializeComponent();

			this.DataContext = this;
		}

		public event PropertyChangedEventHandler PropertyChanged;

		public event RoutedEventHandler Click
		{
			add
			{
				this.AddHandler(ClickEvent, value);
			}

			remove
			{
				this.RemoveHandler(ClickEvent, value);
			}
		}

		public string Text
		{
			get
			{
				return (string)this.GetValue(TextProperty);
			}

			set
			{
				this.SetValue(TextProperty, value);
			}
		}

		public FontAwesomeIcon Icon
		{
			get
			{
				return (FontAwesomeIcon)this.GetValue(IconProperty);
			}
			set
			{
				this.SetValue(IconProperty, value);
			}
		}

		private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
		{
			if (sender is IconButton target)
			{
				target.PropertyChanged?.Invoke(sender, new PropertyChangedEventArgs(e.Property.Name));
			}
		}

		private void OnClick(object sender, RoutedEventArgs e)
		{
			this.RaiseEvent(new RoutedEventArgs(ClickEvent));
		}
	}
}
