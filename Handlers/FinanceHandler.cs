using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;
using System.Linq;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
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

        // Создаем кнопки с категориями
        var buttons = categories.Select(category => InlineKeyboardButton.WithCallbackData(
            category.ToString(), $"expense_category:{category}:{userId}")).ToList();

        // Разбиваем на ряды по 2 кнопки
        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (int i = 0; i < buttons.Count; i += 2)
        {
            rows.Add(buttons.Skip(i).Take(2));
        }

        var inlineKeyboard = new InlineKeyboardMarkup(rows);

        await _botClient.SendMessage(userId,
            "💰 Выберите категорию для расхода:",
            replyMarkup: inlineKeyboard);
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

    public async Task HandleExpenseCallback(long userId, string callbackData, IDatabase redis)
    {
        var parts = callbackData.Split(':');
        if (parts.Length < 3 || parts[0] != "expense_category") return;

        var category = parts[1];
        var originalUserId = long.Parse(parts[2]);

        // Проверяем, что callback от того же пользователя
        if (originalUserId != userId) return;

        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.AnswerCallbackQuery(callbackData, "❌ Нет доступа к финансовому учету");
            return;
        }

        // Создаем кнопки для ввода суммы
        var amountButtons = new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("💰 Ввести сумму", $"expense_amount:{category}:{userId}") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", $"expense_cancel:{userId}") }
        };

        var keyboard = new InlineKeyboardMarkup(amountButtons);

        await _botClient.SendMessage(userId,
            $"✅ Выбрана категория: {category}\n\n💬 Теперь введите сумму расхода (например: 1500)",
            replyMarkup: keyboard);

        await _botClient.AnswerCallbackQuery(callbackData, $"Выбрана категория: {category}");
    }

    public async Task HandleExpenseAmount(long userId, string messageText, IDatabase redis)
    {
        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа к финансовому учету");
            return;
        }

        if (!decimal.TryParse(messageText.Replace(" ", "").Replace(",", "."), NumberStyles.Currency, CultureInfo.InvariantCulture, out var amount))
        {
            await _botClient.SendMessage(userId, "❌ Неверный формат суммы. Введите число (например: 1500)");
            return;
        }

        if (amount <= 0)
        {
            await _botClient.SendMessage(userId, "❌ Сумма должна быть больше нуля");
            return;
        }

        // Получаем выбранную категорию из временного хранилища
        var selectedCategoryKey = $"expense_temp_category:{userId}";
        var category = await redis.StringGetAsync(selectedCategoryKey);

        if (category.IsNullOrEmpty)
        {
            await _botClient.SendMessage(userId, "❌ Сначала выберите категорию через /fin_add_expense");
            return;
        }

        // Создаем запись о расходе
        var expense = new ExpenseRecord
        {
            UserId = userId,
            Category = category.ToString(),
            Amount = amount,
            Description = $"Расход в категории {category}",
            Date = DateTime.UtcNow
        };

        // Сохраняем расход
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");
        await redis.ListRightPushAsync(UtilityKeys.FinExpensesKey(userId, currentMonth),
            JsonSerializer.Serialize(expense));

        // Очищаем временную категорию
        await redis.KeyDeleteAsync(selectedCategoryKey);

        await _botClient.SendMessage(userId,
            $"✅ Расход добавлен!\n📂 Категория: {category}\n💰 Сумма: {amount}₽\n📅 Дата: {expense.Date:dd.MM.yyyy HH:mm}",
            replyMarkup: new ReplyKeyboardRemove());

        // Показываем обновленную аналитику
        await HandleFinAnalytics(userId, $"/fin_analytics {currentMonth}", redis);
    }
}
