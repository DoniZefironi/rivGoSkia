namespace RiveSkia;

public enum RiveInputKind
{
    Bool,
    Number,
    Trigger,
}

/// <summary>
/// Один вход стейт-машины: имя (как задано в редакторе Rive) и тип
/// (Bool/Number/Trigger). Получить список — <see cref="RiveControl.GetInputs"/>.
/// </summary>
public readonly record struct RiveInput(string Name, RiveInputKind Kind);
