using Xunit;

// PDFsharp mantém o resolvedor de fontes em estado global por processo.
// Executar testes em paralelo pode fazer uma operação de PDF inicializar
// esse estado antes de DanfeSharp configurar seu resolvedor.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
