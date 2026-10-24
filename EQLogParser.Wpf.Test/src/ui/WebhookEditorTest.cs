using FontAwesome5;
using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class WebhookEditorTest
  {
    [STATestMethod]
    public void WebhookFieldKeepsErrorWhileEditingAndClearsAfterSuccess()
    {
      using var client = new HttpClient(new SuccessfulHandler());
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "character", [("trigger", "Loot")])["trigger"];
      queue.EnqueueAsync(source, "invalid", "loot", 0).GetAwaiter().GetResult();
      var editor = new WebhookEditor(queue, () => ("character", "trigger"));
      var grid = (Grid)editor.Create(TypeDescriptor.GetProperties(typeof(WebhookField))[nameof(WebhookField.Url)]);
      var property = new PropertyItem { Value = "invalid" };
      editor.Attach(new PropertyViewItem(), property);
      var text = grid.Children.OfType<TextBox>().Single();
      var error = grid.Children.OfType<ImageAwesome>().Single();
      PumpDispatcher();
      Assert.AreEqual("invalid", text.Text);
      Assert.AreEqual(Visibility.Visible, error.Visibility);
      text.Text = "https://discord.com/api/webhooks/123/fixed";
      PumpDispatcher();
      Assert.AreEqual(Visibility.Visible, error.Visibility);
      queue.EnqueueAsync(source, text.Text, "works", 0).GetAwaiter().GetResult();
      PumpDispatcher();
      Assert.AreEqual(Visibility.Collapsed, error.Visibility);
      Assert.IsNull(text.ToolTip);
      editor.Detach(new PropertyViewItem());
    }

    private static void PumpDispatcher()
    {
      var frame = new DispatcherFrame();
      Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
      Dispatcher.PushFrame(frame);
    }

    private sealed class SuccessfulHandler : HttpMessageHandler
    {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public sealed class WebhookField
    {
      public string Url { get; set; } = "invalid";
    }
  }
}