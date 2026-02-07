namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;

public interface IBackendEmitter
{
    string Emit(IrProgram program, EmitContext context);
}
