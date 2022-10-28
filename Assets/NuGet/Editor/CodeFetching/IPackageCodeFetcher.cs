namespace NugetForUnity
{
    internal interface IPackageCodeFetcher
    {
        void FetchCode(NugetPackage package);
    }
}