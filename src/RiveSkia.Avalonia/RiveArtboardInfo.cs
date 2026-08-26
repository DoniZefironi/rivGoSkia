namespace RiveSkia;

// то, что реально есть в файле — имя артборда и имена его стейт-машин, как задал дизайнер
// в редакторе Rive. Не нужно подбирать вслепую: RiveFile.GetArtboards() возвращает список.
public readonly record struct RiveArtboardInfo(string Name, IReadOnlyList<string> StateMachineNames);
