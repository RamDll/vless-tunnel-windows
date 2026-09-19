namespace VlessTunnel.Native;

/// <summary>
/// Ревью п.23: единая точка входа для "doctor" (аварийная зачистка
/// осиротевшего состояния) — WFP-фильтры (<see cref="KillSwitch.Doctor"/>)
/// И свои маршруты (<see cref="RouteManager.RemoveOwnRoutes"/>) вместе.
/// До этого три места (старт службы, IPC-команда doctor, standalone
/// "VlessTunnel.Service.exe doctor") звали KillSwitch.Doctor напрямую —
/// план (3.7) обещает, что удаление снимает "службу, маршруты и
/// WFP-фильтры", но маршруты никто не снимал ни в одном из трёх мест.
/// Один общий метод — чтобы третье, ещё не написанное место не забыло
/// про маршруты тем же способом.
/// </summary>
public static class NetworkDoctor
{
    public readonly record struct Result(int FiltersRemoved, int RoutesRemoved);

    public static Result Run(Action<string>? trace = null)
    {
        var filters = KillSwitch.Doctor(trace);
        var routes = RouteManager.RemoveOwnRoutes(trace);
        return new Result(filters, routes);
    }
}
