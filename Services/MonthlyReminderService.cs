using StackExchange.Redis;
using System.Text.Json;
using Telegram.Bot;

using Ipoteka.Models;

namespace Ipoteka.Services;

public class MonthlyReminderService
{
    private readonly ITelegramBotClient _bot;
    private readonly IDatabase _redis;
    private readonly string[] _readTokens;
    private Timer? _timer;

    public MonthlyReminderService(ITelegramBotClient bot, IDatabase redis, string[] readTokens)
    {
        _bot = bot;
        _redis = redis;
        _readTokens = readTokens;
    }

    public void Start()
    {
        // Расчет времени до следующего 1-го числа
        var now = DateTime.UtcNow;
        var nextDate = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        var initialDelay = nextDate - now;

        _timer = new Timer(SendMonthlyReminders, null, initialDelay, TimeSpan.FromDays(30));
    }

    private async void SendMonthlyReminders(object? state)
    {
        try
        {
            // Получаем данные кредита
            var creditJson = await _redis.StringGetAsync(UtilityKeys.CreditKey());
            if (creditJson.IsNullOrEmpty) return;

            var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;

            // Получаем историю
            var history = await _redis.ListRangeAsync(UtilityKeys.HistoryKey());
            var historyText = history.Length == 0
                ? "История платежей пуста"
                : $"Последние платежи:\n{string.Join("\n", history.Select(h =>
                {
                    var p = JsonSerializer.Deserialize<PaymentRecord>(h!);
                    return $"{p!.Date:dd.MM.yyyy}: -{p.Amount} р";
                }))}";

            var message = $"📅 Ежемесячное обновление:\n" +
                          $"Остаток по кредиту: {credit.CurrentAmount} р\n" +
                          $"{historyText}";

            // Отправляем во все авторизованные чаты
            var chatIds = await _redis.SetMembersAsync(UtilityKeys.AuthChatsKey());
            foreach (var chatIdValue in chatIds)
            {
                if (long.TryParse(chatIdValue.ToString(), out var chatId))
                {
                    try
                    {
                        await _bot.SendMessage(chatId, message);
                    }
                    catch
                    {
                        // Игнорируем ошибки отправки
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отправке ежемесячных уведомлений: {ex.Message}");
        }
    }
}
