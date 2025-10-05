using Ipoteka.Models;

using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

using Telegram.Bot;

namespace Ipoteka.Handlers;

public class IpotekaHandler
{
    public ITelegramBotClient botClient { get; set; }
    private readonly string[] readTokens;
    private readonly string[] writeTokens;

    public IpotekaHandler(ITelegramBotClient botClient, string[] readTokens, string[] writeTokens)
    {
        this.botClient = botClient;
        this.readTokens = readTokens;
        this.writeTokens = writeTokens;
    }

    private static DateTime GetCurrentTimeUtc3()
    {
        return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
    }

    public async Task HandleAuthorize(long chatId, string text, IDatabase redis)
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

    public async Task HandleUserAuthorize(long userId, string text, IDatabase redis)
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

    public async Task HandleSet(long chatId, long userId, string text, IDatabase redis)
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
            LastUpdated = GetCurrentTimeUtc3()
        };

        await redis.StringSetAsync(UtilityKeys.CreditKey(), JsonSerializer.Serialize(creditData));

        // Очищаем историю
        await redis.KeyDeleteAsync(UtilityKeys.HistoryKey());

        // Уведомляем все авторизованные чаты
        await NotifyAllChats($"💰 Установлена новая сумма кредита: {amount} р", redis);
    }

    public async Task HandlePay(long userId, string text, IDatabase redis)
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
        credit.LastUpdated = GetCurrentTimeUtc3();

        // Сохраняем обновленный кредит
        await redis.StringSetAsync(UtilityKeys.CreditKey(), JsonSerializer.Serialize(credit));

        // Сохраняем платеж в историю
        var paymentRecord = new PaymentRecord
        {
            UserId = userId,
            Amount = payment,
            Date = GetCurrentTimeUtc3(),
            NewBalance = credit.CurrentAmount
        };
        await redis.ListRightPushAsync(UtilityKeys.HistoryKey(), JsonSerializer.Serialize(paymentRecord));

        // Уведомление пользователю
        await botClient.SendMessage(userId,
            $"✅ Платеж {payment} р принят!\nНовый остаток: {credit.CurrentAmount} р");

        // Уведомляем все авторизованные чаты
        await NotifyAllChats($"💳 Внесен платеж: {payment} р\nОстаток по кредиту: {credit.CurrentAmount} р", redis);
    }

    public async Task ShowStatus(long chatId, IDatabase redis)
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

    public async Task ShowHistory(long chatId, IDatabase redis)
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

    private async Task NotifyAllChats(string message, IDatabase redis)
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
}
