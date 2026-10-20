using TicketSystem.Services;
using Xunit;

namespace TicketSystem.Tests;

//тества prefix-truncation предложителя за категория - речникът е валидиран отделно върху 200 заглавия (~91% top-1)
public class CategorySuggestionServiceTests
{
    private readonly CategorySuggestionService _sut = new();

    [Fact]
    public void Suggest_EmptyTitle_ReturnsEmptyList()
    {
        var result = _sut.Suggest("");

        Assert.Empty(result);
    }

    [Fact]
    public void Suggest_ClearAccessRequestTitle_RanksAccessCategoryFirst()
    {
        var result = _sut.Suggest("Моля за предоставяне на достъп до профила");

        Assert.NotEmpty(result);
        Assert.Equal("Достъп и права", result[0].TopCategory);
    }

    [Fact]
    public void Suggest_ClearCreditManualInstallmentTitle_RanksCreditOperationsFirst()
    {
        var result = _sut.Suggest("Клиентът плати в брой, добавете вноската ръчно");

        Assert.NotEmpty(result);
        Assert.Equal("Кредитни операции", result[0].TopCategory);
    }

    [Fact]
    public void Suggest_ClearCrmIncidentTitle_RanksSystemIncidentsFirst()
    {
        var result = _sut.Suggest("CRM системата не работи от сутринта");

        Assert.NotEmpty(result);
        Assert.Equal("Системни инциденти", result[0].TopCategory);
        Assert.Equal("CRM недостъпна", result[0].SubCategory);
    }

    [Fact]
    public void Suggest_ClearNewEmployeeTitle_RanksOrganizationalChangesFirst()
    {
        var result = _sut.Suggest("Утре започва нов колега, подгответе всичко");

        Assert.NotEmpty(result);
        Assert.Equal("Организационни промени", result[0].TopCategory);
    }

    [Fact]
    public void Suggest_VagueTitleWithNoKeywords_ReturnsEmptyList()
    {
        var result = _sut.Suggest("Проблем");

        Assert.Empty(result);
    }

    [Fact]
    public void Suggest_NeverReturnsMoreThanRequestedMaxResults()
    {
        // "достъп" сам по себе си съвпада с всичките 3 подкатегории на "Достъп и права"
        var result = _sut.Suggest("Достъп достъп достъп", maxResults: 2);

        Assert.True(result.Count <= 2);
    }

    [Fact]
    public void Suggest_ResultsAreOrderedByScoreDescending()
    {
        var result = _sut.Suggest("Новият служител има нужда от достъп до кредитната система");

        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Score >= result[i].Score);
        }
    }

    [Fact]
    public void Suggest_GenericAdministrativeWordsLikeZayavka_DoNotLeakIntoSuggestions()
    {
        // регресионен тест за реалния бъг, открит при тестването с 200 заглавия:
        // "заявка" беше част от фразата "заявка за достъп" и изтичаше като самостоятелна дума
        var result = _sut.Suggest("Заявка за спешен ремонт на CRM");

        Assert.NotEmpty(result);
        Assert.Equal("Системни инциденти", result[0].TopCategory);
    }
}
