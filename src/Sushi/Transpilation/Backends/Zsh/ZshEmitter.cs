using Sushi.Transpilation.Backends;
using Sushi.Transpilation.Backends.Bash;
using Sushi.Transpilation.IR;

namespace Sushi.Transpilation.Backends.Zsh;

public sealed class ZshEmitter : IBackendEmitter
{
    private readonly BashEmitter _delegate = new(zshMode: true);

    public string Emit(IrProgram program, EmitContext context)
    {
        return _delegate.Emit(program, context);
    }
}
