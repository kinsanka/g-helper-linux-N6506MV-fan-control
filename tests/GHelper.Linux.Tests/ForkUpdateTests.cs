using System.Security.Cryptography;
using System.Text.Json;
using GHelper.Linux.Updates;
using GHelper.Linux.Install;

namespace GHelper.Linux.Tests;

public static class ForkUpdateTests
{
    public static void RunAll()
    {
        Harness.Scenario("ForkUpdate_CustomVersionNumbersAndBuildMetadata", _ =>
        {
            Harness.Assert(ForkRelease.CompareVersions("1.0.97-n6506.10", "1.0.97-n6506.5") > 0, "numeric fork revision");
            Harness.Assert(ForkRelease.CompareVersions("1.0.98-n6506.1", "1.0.97-n6506.10") > 0, "core patch remains meaningful");
            Harness.AssertEqual(0, ForkRelease.CompareVersions("v1.0.97-n6506.5+abc", "1.0.97-n6506.5"), "build metadata ignored");
            Harness.Assert(ForkRelease.CompareVersions("1.0.97", "1.0.97-n6506.5") > 0, "stable semver precedence");
        });
        Harness.Scenario("ForkUpdate_SelectsOwnAppImageAndChecksum", _ =>
        {
            string prefix = ForkRelease.RepositoryUrl + "/releases/download/v1.0.97-n6506.5/";
            string json = JsonSerializer.Serialize(new { tag_name="v1.0.97-n6506.5", html_url=ForkRelease.RepositoryUrl+"/releases/tag/v1.0.97-n6506.5", draft=false,
                assets=new[]{new{name="ghelper",browser_download_url=prefix+"ghelper"}, new{name=ForkRelease.AppImageName,browser_download_url=prefix+ForkRelease.AppImageName},new{name="SHA256SUMS",browser_download_url=prefix+"SHA256SUMS"}} });
            Harness.AssertEqual(ForkRelease.AppImageName,ForkRelease.Parse(json,true).Asset!.Name,"AppImage asset selected");
            Harness.AssertEqual("ghelper",ForkRelease.Parse(json,false).Asset!.Name,"native binary selected");
            bool rejected=false;
            try { ForkRelease.Parse(json.Replace(ForkRelease.Repository,"utajum/g-helper-linux"),true); }
            catch(FormatException){rejected=true;}
            Harness.Assert(rejected,"upstream release cannot replace the fork");
        });
        Harness.Scenario("ForkUpdate_ChecksumRequiredAndTamperRejected", sandbox =>
        {
            string file=Path.Combine(sandbox.TempRoot,"package");File.WriteAllText(file,"validated payload");
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            string? expected=ForkRelease.FindChecksum(hash+"  "+ForkRelease.AppImageName+"\n",ForkRelease.AppImageName);
            Harness.AssertEqual(hash,expected!,"checksum name match");ForkRelease.VerifyFile(file,expected!);
            File.WriteAllText(file,"changed payload");bool rejected=false;
            try{ForkRelease.VerifyFile(file,expected!);}catch(IOException){rejected=true;}
            Harness.Assert(rejected,"corruption cannot reach replacement");
            rejected=false;try{ForkRelease.FindChecksum(hash+"  other-file",ForkRelease.AppImageName);}catch(FormatException){rejected=true;}
            Harness.Assert(rejected,"missing package digest rejected");
        });
        Harness.Scenario("ForkUpdate_AtomicReplacementKeepsOldExecutable", sandbox =>
        {
            string old=Path.Combine(sandbox.TempRoot,"installed"),download=Path.Combine(sandbox.TempRoot,"download");
            File.WriteAllText(old,"old");File.WriteAllText(download,"new");
            AtomicExecutableUpdate.Replace(download,old);
            Harness.AssertEqual("new",File.ReadAllText(old),"new executable");
            Harness.AssertEqual("old",File.ReadAllText(old+".bak"),"rollback copy retained");
            bool rejected=false;try{AtomicExecutableUpdate.Replace(download,old);}catch(IOException){rejected=true;}
            Harness.Assert(rejected && File.ReadAllText(old)=="new","missing download leaves installed program intact");
        });
        Harness.Scenario("AppImageInstall_MenuAutostartAndDataPreserved", sandbox =>
        {
            string home=Path.Combine(sandbox.TempRoot,"Home with spaces"),source=Path.Combine(sandbox.TempRoot,"download.AppImage");
            string target=Path.Combine(home,"Applications",ForkRelease.AppImageName);
            string config=Path.Combine(home,".config/ghelper/n6506-fan-calibration.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);File.WriteAllText(config,"existing calibration");
            File.WriteAllText(source,"appimage-one");
            AppImageUserInstall.Install(source,target,home,"[Desktop Entry]\nExec=ghelper\nName=G-Helper\n",true);
            Harness.Assert(File.Exists(target),"fixed installation location");
            Harness.AssertEqual("/tmp/app.AppImage",AppImageUserInstall.QuoteDesktopExec("/tmp/app.AppImage"),"simple launcher path matches integrity checker");
            string entry=File.ReadAllText(Path.Combine(home,".local/share/applications/ghelper.desktop"));
            Harness.Assert(entry.Contains("Exec=\""+target+"\""),"menu points at stable quoted path");
            Harness.Assert(File.Exists(Path.Combine(home,".config/autostart/ghelper.desktop")),"existing autostart preference honored");
            Harness.AssertEqual("existing calibration",File.ReadAllText(config),"user data unchanged");
            File.WriteAllText(source,"appimage-two");AppImageUserInstall.Install(source,target,home,"Exec=ghelper\n",true);
            Harness.AssertEqual("appimage-one",File.ReadAllText(target+".previous"),"previous installation retained");
        });
        Harness.Scenario("AppImageInstall_RecognizesSymlinkedHome", sandbox =>
        {
            string home=Path.Combine(sandbox.TempRoot,"real-home"),alias=Path.Combine(sandbox.TempRoot,"home-link");
            Directory.CreateDirectory(Path.Combine(home,"Applications"));
            Directory.CreateSymbolicLink(alias,home);
            string target=Path.Combine(home,"Applications",ForkRelease.AppImageName); File.WriteAllText(target,"package");
            Harness.Assert(AppImageUserInstall.IsInstalledLocation(Path.Combine(alias,"Applications",ForkRelease.AppImageName),target),"/home and /var/home aliases must not reinstall endlessly");
        });
        Harness.Scenario("AppImageInstall_DisabledAutostartStaysDisabled", sandbox =>
        {
            string home=Path.Combine(sandbox.TempRoot,"home"),source=Path.Combine(sandbox.TempRoot,"download");
            File.WriteAllText(source,"package");
            AppImageUserInstall.Install(source,Path.Combine(home,"Applications",ForkRelease.AppImageName),home,"Exec=ghelper\n",false);
            Harness.Assert(!File.Exists(Path.Combine(home,".config/autostart/ghelper.desktop")),"no unsolicited login launch");
        });
    }
}
