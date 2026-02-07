namespace Sushi.Transpilation.Intrinsics;

public enum IntrinsicId
{
    Print,
    Println,
    IoReadText,
    IoWriteText,
    IoExists,
    PathJoin,
    PathDirname,
    PathBasename,
    EnvGet,
    EnvSet,
    ProcessArgs,
    ProcessExit,
    ProcessRun,
    ProcessPipeline,
    ProcessFail,
    ProcessRequireSuccess,
    OsCwd,
    OsChdir,
    JsonParse,
    JsonStringify,
    FsGlob,
    HttpGet,
    HttpPost
}
