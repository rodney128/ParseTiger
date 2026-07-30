using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ParseTiger.Execution;
using ParseTiger.Models;

namespace ParseTiger.Reset;

/// <summary>
/// Restores the known clean C# WPF application template while retaining the
/// selected project's name and target framework.
/// </summary>
public sealed class WpfProjectDefaultTemplate : IProjectDefaultTemplate
{
    private static readonly UTF8Encoding TemplateEncoding =
        new(encoderShouldEmitUTF8Identifier: false);

    public string ProjectKind => "WPF";

    public void Restore(ProjectInfo project)
    {
        string projectRoot = Path.GetFullPath(project.Directory);
        string projectFile = Directory
            .EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The selected WPF project does not contain exactly one .csproj file.");

        (string targetFramework, string rootNamespace) =
            ReadProjectIdentity(projectFile, project.Name);
        string ns = MakeIdentifier(rootNamespace);
        var templateFiles = CreateTemplateFiles(
            Path.GetFileName(projectFile), targetFramework, ns);

        var expected = templateFiles.Keys
            .Select(relative => Path.GetFullPath(Path.Combine(projectRoot, relative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string current in ProjectFilePolicy.EnumerateFiles(projectRoot))
        {
            if (!expected.Contains(Path.GetFullPath(current)))
            {
                File.Delete(current);
            }
        }

        foreach ((string relative, string content) in templateFiles)
        {
            string target = PackageExecutor.ResolveInside(projectRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, TemplateEncoding);
        }
    }

    private static Dictionary<string, string> CreateTemplateFiles(
        string projectFileName,
        string targetFramework,
        string ns)
    {
        string nl = Environment.NewLine;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [projectFileName] =
                $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <TargetFramework>{targetFramework}</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>

                </Project>
                """.ReplaceLineEndings(nl) + nl,
            ["App.xaml"] =
                $"""
                <Application x:Class="{ns}.App"
                             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:local="clr-namespace:{ns}"
                             StartupUri="MainWindow.xaml">
                    <Application.Resources>
                         
                    </Application.Resources>
                </Application>
                """.ReplaceLineEndings(nl) + nl,
            ["App.xaml.cs"] =
                $$"""
                using System.Configuration;
                using System.Data;
                using System.Windows;

                namespace {{ns}}
                {
                    /// <summary>
                    /// Interaction logic for App.xaml
                    /// </summary>
                    public partial class App : Application
                    {
                    }

                }
                """.ReplaceLineEndings(nl) + nl,
            ["AssemblyInfo.cs"] =
                """
                using System.Windows;

                [assembly: ThemeInfo(
                    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                                //(used if a resource is not found in the page,
                                                                // or application resource dictionaries)
                    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                                //(used if a resource is not found in the page,
                                                                // app, or any theme specific resource dictionaries)
                )]
                """.ReplaceLineEndings(nl) + nl,
            ["MainWindow.xaml"] =
                $"""
                <Window x:Class="{ns}.MainWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                        xmlns:local="clr-namespace:{ns}"
                        mc:Ignorable="d"
                        Title="MainWindow" Height="450" Width="800">
                    <Grid>

                    </Grid>
                </Window>
                """.ReplaceLineEndings(nl) + nl,
            ["MainWindow.xaml.cs"] =
                $$"""
                using System.Windows;

                namespace {{ns}}
                {
                    /// <summary>
                    /// Interaction logic for MainWindow.xaml
                    /// </summary>
                    public partial class MainWindow : Window
                    {
                        public MainWindow()
                        {
                            InitializeComponent();
                        }
                    }
                }
                """.ReplaceLineEndings(nl) + nl
        };
    }

    private static (string TargetFramework, string RootNamespace)
        ReadProjectIdentity(string projectFile, string fallbackName)
    {
        XDocument document = XDocument.Load(projectFile);
        string? target = document.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("TargetFramework",
                    StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        string? rootNamespace = document.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("RootNamespace",
                    StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        return (
            string.IsNullOrWhiteSpace(target) ? "net10.0-windows" : target,
            string.IsNullOrWhiteSpace(rootNamespace) ? fallbackName : rootNamespace);
    }

    private static string MakeIdentifier(string value)
    {
        string identifier = Regex.Replace(value, @"[^A-Za-z0-9_.]", "_");
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "WpfApp";
        }

        return char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
    }
}
