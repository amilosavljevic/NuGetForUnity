namespace NugetForUnity
{
    internal interface IPackageCodeLinker
    {
        bool IsLinked(NugetPackage package);
        
        void LinkCode(NugetPackage package);
        void UnlinkCode(NugetPackage package);
    }
}