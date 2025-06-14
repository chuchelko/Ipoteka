using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var host = builder.Build();

// Конфигурация Redis
var redisConnection = await ConnectionMultiplexer.ConnectAsync(
    Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379");
var redis = redisConnection.GetDatabase();
var logger = LoggerFactory.Create(logging => logging.AddConsole()).CreateLogger<Program>();

// Инициализация бота
var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new Exception("BOT_TOKEN environment variable is not set");

logger.LogInformation($"Bot token: {token}");

var botClient = new TelegramBotClient(token);

// Запуск сервиса ежемесячных уведомлений
var reminderService = new MonthlyReminderService(botClient, redis);
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
    if (update.Message is not { } message || message.Text is not { } text)
        return;

    var chatId = message.Chat.Id;
    var command = text.Split(' ')[0].ToLower();

    switch (command)
    {
        case "/start":
            await bot.SendMessage(chatId,
                "📊 Бот для отслеживания кредита\n\n" +
                "Доступные команды:\n" +
                "/set [сумма] - установить сумму кредита\n" +
                "/pay [сумма] - внести платеж\n" +
                "/status - текущий остаток\n" +
                "/history - история платежей");
            break;

        case "/set":
            await SetCreditAmount(chatId, text);
            break;

        case "/pay":
            await ProcessPayment(chatId, text);
            break;

        case "/status":
            await ShowStatus(chatId);
            break;

        case "/history":
            await ShowHistory(chatId);
            break;

        default:
            await bot.SendMessage(chatId, "Неизвестная команда. Используйте /start для справки");
            break;
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

// Redis ключи
static string GetCreditKey(long chatId) => $"credit:{chatId}";
static string GetHistoryKey(long chatId) => $"history:{chatId}";

// Работа с кредитом
async Task SetCreditAmount(long chatId, string text)
{
    var parts = text.Split(' ');
    if (parts.Length < 2 || !decimal.TryParse(parts[1], NumberStyles.Currency, CultureInfo.InvariantCulture, out var amount))
    {
        await botClient.SendMessage(chatId, "❌ Неверный формат. Используйте: /set 100000");
        return;
    }

    var creditData = new CreditData
    {
        InitialAmount = amount,
        CurrentAmount = amount
    };

    await redis.StringSetAsync(GetCreditKey(chatId), JsonSerializer.Serialize(creditData));
    await redis.KeyDeleteAsync(GetHistoryKey(chatId)); // Очищаем историю

    await botClient.SendMessage(chatId, $"✅ Начальная сумма кредита установлена: {amount:C}");
}

async Task ProcessPayment(long chatId, string text)
{
    var creditJson = await redis.StringGetAsync(GetCreditKey(chatId));
    if (creditJson.IsNullOrEmpty)
    {
        await botClient.SendMessage(chatId, "❌ Сначала установите сумму кредита (/set [сумма])");
        return;
    }

    var parts = text.Split(' ');
    if (parts.Length < 2 || !decimal.TryParse(parts[1], NumberStyles.Currency, CultureInfo.InvariantCulture, out var payment))
    {
        await botClient.SendMessage(chatId, "❌ Неверный формат. Используйте: /pay 15000");
        return;
    }

    var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;
    credit.CurrentAmount -= payment;

    // Сохраняем обновленный кредит
    await redis.StringSetAsync(GetCreditKey(chatId), JsonSerializer.Serialize(credit));

    // Сохраняем платеж в историю
    var paymentRecord = new PaymentRecord
    {
        Amount = payment,
        Date = DateTime.UtcNow,
        NewBalance = credit.CurrentAmount
    };
    await redis.ListRightPushAsync(GetHistoryKey(chatId), JsonSerializer.Serialize(paymentRecord));

    await botClient.SendMessage(chatId,
        $"✅ Платеж {payment:C} принят!\n" +
        $"Новый остаток: {credit.CurrentAmount:C}");
}

async Task ShowStatus(long chatId)
{
    var creditJson = await redis.StringGetAsync(GetCreditKey(chatId));
    if (creditJson.IsNullOrEmpty)
    {
        await botClient.SendMessage(chatId, "❌ Кредит не установлен. Используйте /set [сумма]");
        return;
    }

    var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;
    await botClient.SendMessage(chatId, $"💳 Текущий остаток по кредиту: {credit.CurrentAmount:C}");
}

async Task ShowHistory(long chatId)
{
    var history = await redis.ListRangeAsync(GetHistoryKey(chatId));
    if (history.Length == 0)
    {
        await botClient.SendMessage(chatId, "📭 История платежей пуста");
        return;
    }

    var response = "📜 История платежей:\n";
    foreach (var item in history)
    {
        var payment = JsonSerializer.Deserialize<PaymentRecord>(item!);
        response += $"{payment!.Date:dd.MM.yyyy}: -{payment.Amount:C} → {payment.NewBalance:C}\n";
    }

    await botClient.SendMessage(chatId, response);
}

// Модели данных
record CreditData
{
    public decimal InitialAmount { get; set; }
    public decimal CurrentAmount { get; set; }
}

record PaymentRecord
{
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public decimal NewBalance { get; set; }
}

// Сервис ежемесячных уведомлений
class MonthlyReminderService
{
    private readonly ITelegramBotClient _bot;
    private readonly IDatabase _redis;
    private Timer? _timer;

    public MonthlyReminderService(ITelegramBotClient bot, IDatabase redis)
    {
        _bot = bot;
        _redis = redis;
    }

    public void Start()
    {
        // Расчет времени до следующего 1-го числа
        var now = DateTime.UtcNow;
        var nextDate = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        var initialDelay = nextDate - now;

        _timer = new Timer(SendReminders, null, initialDelay, TimeSpan.FromDays(30));
    }

    private async void SendReminders(object? state)
    {
        // Получаем все ключи кредитов
        var keys = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First())
            .Keys(pattern: "credit:*")
            .ToArray();

        foreach (var key in keys)
        {
            var chatId = long.Parse(key.ToString().Split(':')[1]);
            var creditJson = await _redis.StringGetAsync(key);
            if (creditJson.IsNullOrEmpty) continue;

            var credit = JsonSerializer.Deserialize<CreditData>(creditJson!)!;
            if (credit.CurrentAmount > 0)
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: $"📅 Ежемесячное обновление:\nОстаток по кредиту: {credit.CurrentAmount:C}"
                );
            }
        }
    }
}