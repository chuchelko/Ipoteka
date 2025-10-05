using Microsoft.Extensions.Hosting;

using StackExchange.Redis;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Ipoteka.Services;
using System.Globalization;

var builder = Host.CreateApplicationBuilder(args);
var host = builder.Build();
Console.WriteLine(Environment.GetEnvironmentVariable("REDIS_URL"));
Console.WriteLine(Environment.GetEnvironmentVariable("READ_TOKENS"));
Console.WriteLine(Environment.GetEnvironmentVariable("WRITE_TOKENS"));
Console.WriteLine(Environment.GetEnvironmentVariable("FIN_TOKEN"));

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
var finToken = Environment.GetEnvironmentVariable("FIN_TOKEN")?.Split(',')
                 ?? throw new Exception("FIN_TOKEN not set");

// Инициализация хендлера команд
var ipotekaHandler = new Ipoteka.Handlers.IpotekaHandler(botClient, readTokens, writeTokens);
var financeHandler = new Ipoteka.Handlers.FinanceHandler(botClient, finToken);

// Запуск сервиса ежемесячных уведомлений
var reminderService = new MonthlyReminderService(botClient, redis, readTokens);
reminderService.Start();
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
    if (update.Message is { } message)
    {
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
                    await ipotekaHandler.HandleAuthorize(chatId, text, redis);
                    break;

                case "/user_authorize":
                    await ipotekaHandler.HandleUserAuthorize(userId, text, redis);
                    break;

                case "/set":
                    await ipotekaHandler.HandleSet(chatId, userId, text, redis);
                    break;

                case "/pay":
                    await ipotekaHandler.HandlePay(userId, text, redis);
                    break;

                case "/status":
                    await ipotekaHandler.ShowStatus(chatId, redis);
                    break;

                case "/history":
                    await ipotekaHandler.ShowHistory(chatId, redis);
                    break;

                case "/fin_auth":
                    await financeHandler.HandleFinAuth(userId, text, redis);
                    break;

                case "/fin_add_category":
                    await financeHandler.HandleFinAddCategory(userId, text, redis);
                    break;

                case "/fin_categories":
                    await financeHandler.HandleFinListCategories(userId, redis);
                    break;

                case "/fin_set_budget":
                    await financeHandler.HandleFinSetBudget(userId, text, redis);
                    break;

                case "/fin_add_expense":
                    await financeHandler.HandleFinAddExpense(userId, redis);
                    break;

                case "/fin_analytics":
                    await financeHandler.HandleFinAnalytics(userId, text, redis);
                    break;

                case "/fin_history":
                    await financeHandler.HandleExpenseHistory(userId, text, redis);
                    break;

                case "/gav":
                    await botClient.SendMessage(chatId, "ГАВ");
                    break;

                default:
                    // Проверяем, является ли сообщение суммой расхода
                    if (decimal.TryParse(text.Replace(" ", "").Replace(",", "."), NumberStyles.Currency, CultureInfo.InvariantCulture, out _))
                    {
                        await financeHandler.HandleExpenseAmount(userId, text, redis);
                    }
                    // Проверяем, является ли сообщение планируемым расходом категории
                    else if (IsCategoryPlannedAmountInput(userId, text, redis))
                    {
                        await financeHandler.HandleCategoryPlannedAmount(userId, text, redis);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Неизвестная команда. Используйте /start для справки");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обработки команды: {ex.Message}");
            await botClient.SendMessage(chatId, $"❌ Ошибка: {ex.Message}");
        }
    }
    else if (update.CallbackQuery is { } callbackQuery)
    {
        var userId = callbackQuery.From.Id;
        var callbackData = callbackQuery.Data ?? string.Empty;

        try
        {
            // Обрабатываем callback для выбора категории расхода
            if (callbackData.StartsWith("expense_category:"))
            {
                await financeHandler.HandleExpenseCallback(userId, callbackData, redis);
            }
            else if (callbackData.StartsWith("expense_amount:"))
            {
                var parts = callbackData.Split(':');
                if (parts.Length >= 3)
                {
                    var category = parts[1];
                    var tempCategoryKey = $"expense_temp_category:{userId}";
                    await redis.StringSetAsync(tempCategoryKey, category);

                    await botClient.AnswerCallbackQuery(callbackData, null, showAlert: false);
                    await botClient.SendMessage(userId, "💬 Введите сумму расхода (например: 1500):");
                }
            }
            else if (callbackData.StartsWith("expense_cancel:"))
            {
                var tempCategoryKey = $"expense_temp_category:{userId}";
                await redis.KeyDeleteAsync(tempCategoryKey);

                await botClient.AnswerCallbackQuery(callbackData, null, showAlert: false);
                await botClient.SendMessage(userId, "❌ Добавление расхода отменено");
            }
            // Обрабатываем callback для истории расходов
            else if (callbackData.Contains("expense_history") || callbackData.Contains("expense_edit") || callbackData.Contains("expense_delete"))
            {
                await financeHandler.HandleExpenseHistoryCallback(userId, callbackData, redis);
            }
            // Обрабатываем callback для выбора месяца аналитики
            else if (callbackData.StartsWith("analytics_month:"))
            {
                var parts = callbackData.Split(':');
                if (parts.Length >= 3)
                {
                    var month = parts[1];
                    await botClient.AnswerCallbackQuery(callbackData, null, showAlert: false);
                    await financeHandler.ShowExpenseAnalytics(userId, month, redis);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обработки callback: {ex.Message}");
        }
    }
}

async Task SendHelp(long chatId)
{
    await botClient.SendMessage(chatId,
        "🏦 Бот управления кредитом\n\n" +
        "📋 Доступные команды:\n\n" +
        "🔐 Авторизация:\n" +
        "/authorize [токен] - авторизовать чат (чтение)\n" +
        "/user_authorize [токен] - авторизовать себя (запись)\n\n" +
        "💰 Управление кредитом:\n" +
        "/set [сумма] - установить сумму кредита\n" +
        "/pay [сумма] - внести платеж\n" +
        "/status - текущий остаток\n" +
        "/history - история платежей\n\n" +
        "💳 Финансовый учет:\n" +
        "/fin_auth [токен] - авторизация для финансов\n" +
        "/fin_add_category [категория] - добавить категорию расходов\n" +
        "/fin_categories - показать все категории\n" +
        "/fin_set_budget [месяц] [сумма] - установить бюджет на месяц\n" +
        "/fin_add_expense - добавить расход (по кнопкам)\n" +
        "/fin_history [месяц] - история расходов с редактированием\n" +
        "/fin_analytics - аналитика расходов с графиком\n\n" +
        "🎭 Прочее:\n" +
        "/gav - гаф");
}

bool IsCategoryPlannedAmountInput(long userId, string text, IDatabase redis)
{
    // Проверяем, есть ли временные категории для этого пользователя
    var pattern = $"temp_category:{userId}:*";
    var server = redis.Multiplexer.GetServer(redis.Multiplexer.GetEndPoints()[0]);
    var tempKeys = server.Keys(pattern: pattern).ToArray();

    return tempKeys.Length > 0 && decimal.TryParse(text.Replace(" ", "").Replace(",", "."), NumberStyles.Currency, CultureInfo.InvariantCulture, out _);
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
