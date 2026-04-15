using Autodesk.Revit.UI;

namespace RevitFamilyBuilder
{
    public class RevitFamilyBuilder
    {
        public void Execute(string message, UIApplication uiApp)
        {
            MainWindow window = new MainWindow(uiApp);
            window.ShowDialog();
        }
    }
}