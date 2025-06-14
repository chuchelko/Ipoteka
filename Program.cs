using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
var host = builder.Build();

// Конфигурация Redis
var redisConnection = await ConnectionMultiplexer.ConnectAsync(
    Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379");
var redis = redisConnection.GetDatabase();

// Инициализация бота
var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new Exception("BOT_TOKEN environment variable is not set");
var botClient = new TelegramBotClient(token);

// Загрузка токенов доступа
var readTokens = Environment.GetEnvironmentVariable("READ_TOKENS")?.Split(',')
                 ?? throw new Exception("READ_TOKENS not set");
var writeTokens = Environment.GetEnvironmentVariable("WRITE_TOKENS")?.Split(',')
                  ?? throw new Exception("WRITE_TOKENS not set");

// Запуск сервиса ежемесячных уведомлений
var reminderService = new MonthlyReminderService(botClient, redis, readTokens);
reminderService.Start();

// Обработка входящих сообщений
var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = []
};

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions
);

Console.WriteLine("Бот запущен!");
await host.RunAsync();

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is not { } message) return;

    var chatId = message.Chat.Id;
    var userId = message.From?.Id ?? 0;
    var text = message.Text ?? string.Empty;
    var command = text.Split(' ')[0].ToLower();

    try
    {
        switch (command)
        {
            case "/start":
                await SendHelp(chatId);
                break;

            case "/authorize":
                await HandleAuthorize(chatId, text, redis);
                break;

            case "/user_authorize":
                await HandleUserAuthorize(userId, text, redis);
                break;

            case "/set":
                await HandleSet(chatId, userId, text, redis);
                break;

            case "/pay":
                await HandlePay(userId, text, redis);
                break;

            case "/status":
                await ShowStatus(chatId, redis);
                break;

            case "/history":
                await ShowHistory(chatId, redis);
                break;

            case "/gav":
                await botClient.SendMessage(chatId, "ГАВ");
                break;

            default:
                await botClient.SendMessage(chatId, "Неизвестная команда. Используйте /start для справки");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка обработки команды: {ex.Message}");
        await botClient.SendMessage(chatId, $"❌ Ошибка: {ex.Message}");
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
{
    var errorMessage = exception switch
    {
        ApiRequestException apiRequestException
            => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
        _ => exception.ToString()
    };

    Console.WriteLine(errorMessage);
    return Task.CompletedTask;
}

async Task SendHelp(long chatId)
{
    await botClient.SendMessage(chatId,
        "🏦 Бот управления кредитом\n\n" +
        "Доступные команды:\n" +
        "/authorize [токен] - авторизовать чат (чтение)\n" +
        "/user_authorize [токен] - авторизовать себя (запись)\n" +
        "/set [сумма] - установить сумму кредита\n" +
        "/pay [сумма] - внести платеж\n" +
        "/status - текущий остаток\n" +
        "/history - история платежей");
}

async Task HandleAuthorize(long chatId, string text, IDatabase redis)
{
    var parts = text.Split(' ');
    if (parts.Length < 2)
    {
        await botClient.SendMessage(chatId, "❌ Не указан токен. Используйте: /authorize ваш_токен");
        return;
    }

    var token = parts[1].Trim();
    if (readTokens.Contains(token) || writeTokens.Contains(token))
    {
        await redis.SetAddAsync(UtilityKeys.AuthChatsKey(), chatId);
        await botClient.SendMessage(chatId, "✅ Чат авторизован для чтения!");
    }
    else
    {
        await botClient.SendMessage(chatId, "❌ Неверный токен авторизации");
    }
}

async Task HandleUserAuthorize(long userId, string text, IDatabase redis)
{
    var parts = text.Split(' ');
    if (parts.Length < 2)
    {
        return; // Пользователь не может ответить в группе, поэтому ошибку не отправляем
    }

    var token = parts[1].Trim();
    if (writeTokens.Contains(token))
    {
        await redis.SetAddAsync(UtilityKeys.AuthUsersKey(), userId);
        await botClient.SendMessage(userId, "✅ Вы авторизованы для внесения платежей!");
    }
    else
    {
        await botClient.SendMessage(userId, "❌ Неверный токен авторизации");
    }
}

async Task HandleSet(long chatId, long userId, string text, IDatabase redis)
{
    // Проверка прав пользователя
    if (!await redis.SetContainsAsync(UtilityKeys.AuthUsersKey(), userId))
    {
        await botClient.SendMessage(chatId, "❌ У вас нет прав на установку суммы кредита");
        return;
    }

    var parts = text.Split(' ');
    if (parts.Length < 2 || !decimal.TryParse(parts[1], NumberStyles.Currency, CultureInfo.InvariantCulture, out var amount))
    {
        await botClient.SendMessage(chatId, "❌ Неверный формат. Используйте: /set 100000");
        return;
    }

    // Сохраняем сумму кредита
    var creditData = new CreditData
    {
        InitialAmount = amount,
        CurrentAmount = amount,
        LastUpdated = DateTime.UtcNow
    };

    await redis.StringSetAsync(UtilityKeys.CreditKey(), JsonSerializer.Serialize(creditData));

    // Очищаем историю
    await redis.KeyDeleteAsync(UtilityKeys.HistoryKey());

    await botClient.SendMessage(chatId, $"✅ Начальная сумма кредита установлена: {amount} р");

    // Уведомляем все авторизованные чаты
    await NotifyAllChats($"💰 Установлена новая сумма кредита: {amount} р", redis);
}

async Task HandlePay(long userId, string text, IDatabase redis)
{
    // Проверка прав пользователя
    if (!await redis.SetContainsAsync(UtilityKeys.AuthUsersKey(), userId))
    {
        // Отправляем в личку пользователю
        await botClient.SendMessage(userId,
            "❌ У вас нет прав на внесение платежа. Используйте /user_authorize [токен]");
        return;
    }

    var parts = text.Split(' ');
    if (parts.Length < 2 || !decimal.TryParse(parts[1], NumberStyles.Currency, CultureInfo.InvariantCulture, out var payment))
    {
        await botClient.SendMessage(userId, "❌ Неверный формат. Используйте: /pay 15000");
        return;
    }

    // Получаем текущие данные кредита
    var creditJson = await redis.StringGetAsync(UtilityKeys.CreditKey());
    if (creditJson.IsNullOrEmpty)
    {
        await botClient.SendMessage(userId, "❌ Сначала установите сумму кредита (/set [сумма])");
        return;
    }

    var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;
    credit.CurrentAmount -= payment;
    credit.LastUpdated = DateTime.UtcNow;

    // Сохраняем обновленный кредит
    await redis.StringSetAsync(UtilityKeys.CreditKey(), JsonSerializer.Serialize(credit));

    // Сохраняем платеж в историю
    var paymentRecord = new PaymentRecord
    {
        UserId = userId,
        Amount = payment,
        Date = DateTime.UtcNow,
        NewBalance = credit.CurrentAmount
    };
    await redis.ListRightPushAsync(UtilityKeys.HistoryKey(), JsonSerializer.Serialize(paymentRecord));

    // Уведомление пользователю
    await botClient.SendMessage(userId,
        $"✅ Платеж {payment} р принят!\nНовый остаток: {credit.CurrentAmount} р");

    // Уведомляем все авторизованные чаты
    await NotifyAllChats($"💳 Внесен платеж: {payment} р\nОстаток по кредиту: {credit.CurrentAmount} р", redis);
}

async Task ShowStatus(long chatId, IDatabase redis)
{
    // Проверка авторизации чата
    if (!await redis.SetContainsAsync(UtilityKeys.AuthChatsKey(), chatId))
    {
        await botClient.SendMessage(chatId, "❌ Чат не авторизован. Используйте /authorize [токен]");
        return;
    }

    var creditJson = await redis.StringGetAsync(UtilityKeys.CreditKey());
    if (creditJson.IsNullOrEmpty)
    {
        await botClient.SendMessage(chatId, "❌ Сумма кредита не установлена");
        return;
    }

    var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;
    await botClient.SendMessage(chatId, $"💳 Текущий остаток по кредиту: {credit.CurrentAmount} р");
}

async Task ShowHistory(long chatId, IDatabase redis)
{
    // Проверка авторизации чата
    if (!await redis.SetContainsAsync(UtilityKeys.AuthChatsKey(), chatId))
    {
        await botClient.SendMessage(chatId, "❌ Чат не авторизован. Используйте /authorize [токен]");
        return;
    }

    var history = await redis.ListRangeAsync(UtilityKeys.HistoryKey());
    if (history.Length == 0)
    {
        await botClient.SendMessage(chatId, "📭 История платежей пуста");
        return;
    }

    var response = "📜 История платежей:\n";
    foreach (var item in history)
    {
        var payment = JsonSerializer.Deserialize<PaymentRecord>(item!);
        response += $"{payment!.Date:dd.MM.yyyy}: -{payment.Amount} р → {payment.NewBalance} р\n";
    }

    await botClient.SendMessage(chatId, response);
}

async Task NotifyAllChats(string message, IDatabase redis)
{
    var chatIds = await redis.SetMembersAsync(UtilityKeys.AuthChatsKey());
    foreach (var chatIdValue in chatIds)
    {
        if (long.TryParse(chatIdValue.ToString(), out var chatId))
        {
            try
            {
                await botClient.SendMessage(chatId, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки в чат {chatId}: {ex.Message}");
                // Автоматически удаляем неработающие чаты
                await redis.SetRemoveAsync(UtilityKeys.AuthChatsKey(), chatIdValue);
            }
        }
    }
}

// Модели данных
// Переместите функции CreditKey, UtilityKeys.HistoryKey, UtilityKeys.AuthChatsKey и UtilityKeys.AuthUsersKey из верхнего уровня в статический класс UtilityKeys
static class UtilityKeys
{
    public static string CreditKey() => "credit:global";
    public static string HistoryKey() => "history:global";
    public static string AuthChatsKey() => "auth:chats";
    public static string AuthUsersKey() => "auth:users";
}

record CreditData
{
    public decimal InitialAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public DateTime LastUpdated { get; set; }
}

record PaymentRecord
{
    public long UserId { get; set; }
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public decimal NewBalance { get; set; }
}

// Сервис ежемесячных уведомлений
class MonthlyReminderService
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
                : $"Последние платежи:\n{string.Join("\n", history.Select(h => {
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
