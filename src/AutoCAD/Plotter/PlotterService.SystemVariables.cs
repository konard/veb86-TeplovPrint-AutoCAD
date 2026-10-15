using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using HostMgd.PlottingServices;
using CadApp = HostMgd.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    
    private sealed class PlotSystemVariables : IDisposable
    {
        private readonly List<(string Name, object? Value)> _oldValues = new();
        private bool _disposed;

        
        public static PlotSystemVariables Apply(bool plotTransparency)
        {
            var variables = new PlotSystemVariables();
            variables.Set("BACKGROUNDPLOT", 0);
            variables.Set("PUBLISHCOLLATE", 0);
            variables.Set("PDFSHX", 0);

            variables.Set("PLOTTRANSPARENCYOVERRIDE", plotTransparency ? 2 : 1);
            return variables;
        }

        
        private void Set(string name, object value)
        {
            try
            {
                var oldValue = CadApp.GetSystemVariable(name);
                PrintSessionLog.Step($"SetSystemVariable({name}: {oldValue} -> {value})");
                CadApp.SetSystemVariable(name, value);
                _oldValues.Add((name, oldValue));
            }
            catch (Exception ex)
            {
                PrintSessionLog.Write("WARN", $"Системная переменная {name} не установлена: {ex.GetType().Name}: {ex.Message}");
            }
        }

        
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _oldValues.Count - 1; i >= 0; i--)
            {
                try
                {
                    CadApp.SetSystemVariable(_oldValues[i].Name, _oldValues[i].Value);
                }
                catch
                {
                }
            }
        }
    }
}
