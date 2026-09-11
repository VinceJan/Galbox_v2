using System.Windows.Automation;
var root = AutomationElement.RootElement;
Console.WriteLine($"UIA OK: root={root.Current.Name}");
return 0;
