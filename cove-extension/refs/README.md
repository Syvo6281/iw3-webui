# Reference assemblies

The extension compiles against four of Cove's own assemblies. They are not
redistributed here — take them from your own Cove container:

```sh
docker cp Cove:/opt/cove/Cove.Sdk.dll     .
docker cp Cove:/opt/cove/Cove.Plugins.dll .
docker cp Cove:/opt/cove/Cove.Core.dll    .
docker cp Cove:/opt/cove/Cove.Data.dll    .
```

Take them from the **running container**, not from a source checkout. A
deployed Cove build lags its own main branch, and the plugin contract differs
between the two — compiling against the newer one produces an extension that
loads and then fails at the first call.

These are reference-only (`<Private>false</Private>`): nothing is copied into
the build output. The host provides them at runtime.
