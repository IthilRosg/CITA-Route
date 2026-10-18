using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RoutePilot;

public sealed class RateChart : FrameworkElement
{
    private List<RatePoint> _points=new();
    public void SetPoints(IEnumerable<RatePoint> points){_points=points.ToList();InvalidateVisual();}
    private static readonly Brush Muted=new SolidColorBrush(Color.FromRgb(120,139,154));
    private static readonly Brush Down=new SolidColorBrush(Color.FromRgb(192,239,134));
    private static readonly Brush Up=new SolidColorBrush(Color.FromRgb(119,182,255));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=Math.Max(20,ActualWidth-65),h=Math.Max(20,ActualHeight-28);
        double max=Math.Max(128*1024,_points.Select(p=>Math.Max(p.Upload,p.Download)).DefaultIfEmpty().Max()*1.18);
        void Label(string text,double x,double y)=>dc.DrawText(new FormattedText(text,CultureInfo.GetCultureInfo("ru-RU"),FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,Muted,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        for(int i=0;i<=3;i++)
        {
            double y=8+i*(h-8)/3;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(38,51,62)),1),new Point(0,y),new Point(w,y));
            Label(Format.Rate(max*(1-i/3.0)),w+9,y-7);
        }
        Label("−60 с",0,h+8);Label("сейчас",w-36,h+8);
        if(_points.Count<2)
        {
            Label("График появится после подключения",Math.Max(0,w/2-103),h/2-6);
            return;
        }
        var end=DateTime.UtcNow;var start=end.AddSeconds(-60);
        foreach(bool upload in new[]{false,true})
        {
            var line=new StreamGeometry();
            using(var ctx=line.Open())
            {
                bool first=true;
                foreach(var p in _points.Where(p=>p.Time>=start))
                {
                    double x=Math.Clamp((p.Time-start).TotalSeconds/60,0,1)*w;
                    double y=h-(upload?p.Upload:p.Download)/max*(h-8);
                    if(first){ctx.BeginFigure(new Point(x,y),false,false);first=false;}else ctx.LineTo(new Point(x,y),true,false);
                }
            }
            dc.DrawGeometry(null,new Pen(upload?Up:Down,2.2),line);
        }
    }
}
