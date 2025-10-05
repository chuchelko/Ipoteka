using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;
using System.Linq;

using Telegram.Bot;
using Ipoteka.Models;

namespace Ipoteka.Handlers;

public class FinanceHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly string[] _finToken;

    public FinanceHandler(ITelegramBotClient botClient, string[] finToken)
    {
        _botClient = botClient;
        _finToken = finToken;
    }

    public async Task HandleFinAuth(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ');
        if (parts.Length < 2)
        {
            await _botClient.SendMessage(userId, "❌ Укажите токен. Пример: /fin_auth ваш_токен");
            return;
        }

        if (_finToken.Contains(text.Split(' ')[1].Trim()))
        {
            await redis.SetAddAsync(UtilityKeys.FinAuthUsersKey(), userId);
            await _botClient.SendMessage(userId, "✅ Вы получили доступ к финансовому учету");
        }
        else
        {
            await _botClient.SendMessage(userId, "❌ Неверный токен");
        }
    }

    public async Task HandleFinAddCategory(long userId, string text, IDatabase redis)
    {
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа. Сначала авторизуйтесь /fin_auth [токен]");
            return;
        }

        var parts = text.Split(' ', 2);
        if (parts.Length < 2)
        {
            await _botClient.SendMessage(userId, "❌ Укажите категорию. Пример: /fin_add_category Еда");
            return;
        }

        var category = parts[1].Trim();
        await redis.SetAddAsync(UtilityKeys.FinCategoriesKey(userId), category);
        await _botClient.SendMessage(userId, $"✅ Категория «{category}» добавлена");
    }

    public async Task HandleFinListCategories(long userId, IDatabase redis)
    {
        var categories = await redis.SetMembersAsync(UtilityKeys.FinCategoriesKey(userId));
        if (categories.Length == 0)
        {
            await _botClient.SendMessage(userId, "📂 Категорий пока нет. Добавьте через /fin_add_category");
            return;
        }

        var list = string.Join("\n", categories.Select(c => $"• {c}"));
        await _botClient.SendMessage(userId, $"📂 Ваши категории:\n{list}");
    }

    public async Task HandleFinSetBudget(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ');
        if (parts.Length < 3 ||
            !decimal.TryParse(parts[2], CultureInfo.InvariantCulture, out var limit))
        {
            await _botClient.SendMessage(userId, "❌ Формат: /fin_set_budget 2025-10 50000");
            return;
        }

        var month = parts[1].Trim();
        var data = new { Limit = limit, Spent = 0m };
        await redis.StringSetAsync(UtilityKeys.FinBudgetKey(userId, month), JsonSerializer.Serialize(data));

        await _botClient.SendMessage(userId, $"✅ Бюджет на {month} установлен: {limit}₽");
    }

    public async Task HandleFinAddExpense(long userId, IDatabase redis)
    {
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа. Авторизуйтесь /fin_auth [токен]");
            return;
        }

        var categories = await redis.SetMembersAsync(UtilityKeys.FinCategoriesKey(userId));
        if (categories.Length == 0)
        {
            await _botClient.SendMessage(userId, "📂 Сначала добавьте категории через /fin_add_category");
            return;
        }

        // Пример — позже заменим на inline кнопки
        await _botClient.SendMessage(userId, "💡 Скоро появится режим добавления трат по кнопкам.");
    }

    public async Task HandleFinAnalytics(long userId, string text, IDatabase redis)
    {
        var month = text.Split(' ').ElementAtOrDefault(1) ?? DateTime.UtcNow.ToString("yyyy-MM");
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var list = await redis.ListRangeAsync(key);
        if (list.Length == 0)
        {
            await _botClient.SendMessage(userId, $"📭 Нет трат за {month}");
            return;
        }

        var expenses = list
            .Select(j => JsonSerializer.Deserialize<ExpenseRecord>(j!)!)
            .GroupBy(e => e.Category)
            .Select(g => new { Category = g.Key, Sum = g.Sum(x => x.Amount) })
            .ToList();

        var total = expenses.Sum(x => x.Sum);
        var textReport = string.Join("\n", expenses.Select(e => $"{e.Category}: {e.Sum}₽ ({e.Sum / total:P0})"));
        await _botClient.SendMessage(userId, $"📊 Расходы за {month}:\n{textReport}");
    }
}
