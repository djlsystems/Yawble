using System.Runtime.CompilerServices;

// `ContainerHost.ManagerName` is internal - restated from `TeamRegistry.DefaultManagerName` because
// `Harness.Containers` cannot reference the Host. A Host test asserts the two agree,
// which needs this to reach the constant at all.
[assembly: InternalsVisibleTo("Harness.Host.Tests")]
