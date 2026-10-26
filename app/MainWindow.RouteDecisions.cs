using System.Windows;
using System.Windows.Controls;

namespace RoutePilot;

public partial class MainWindow
{
    void RouteDecisions_Expanded(object sender,RoutedEventArgs e){if(_ready)RefreshPolicyState();}
    void RouteDecision_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {if(RouteDecisionsGrid.SelectedItem is RouteDecisionRow row)RouteDecisionDetail.Text=row.Detail;}
}
