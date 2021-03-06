using System.Linq;
using System.Threading.Tasks;
using Acs1;
using Acs3;
using AElf.Contracts.Association;
using AElf.Contracts.Parliament;
using AElf.Contracts.Referendum;
using AElf.Kernel;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;
using Volo.Abp.Threading;
using Xunit;
using CreateOrganizationInput = AElf.Contracts.Parliament.CreateOrganizationInput;

namespace AElf.Contracts.MultiToken
{
    public class MultiTokenAuthorizedTest : MultiTokenContractCrossChainTestBase
    {
        public MultiTokenAuthorizedTest()
        {
            AsyncHelper.RunSync(InitializeTokenContract);
        }

        private async Task InitializeTokenContract()
        {
            var initResult = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.Initialize), new InitializeInput());
            initResult.Status.ShouldBe(TransactionResultStatus.Mined);
            var initControllerResult = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.InitializeAuthorizedController), new Empty());
            initControllerResult.Status.ShouldBe(TransactionResultStatus.Mined);
        }

        [Fact]
        public async Task Controller_Transfer_For_Symbol_To_Pay_Tx_Fee()
        {
            var primaryTokenRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetPrimaryTokenSymbol), new Empty());
            var primarySymbol = new StringValue();
            primarySymbol.MergeFrom(primaryTokenRet.ReturnValue);
            var newSymbolList = new SymbolListToPayTXSizeFee();
            newSymbolList.SymbolsToPayTxSizeFee.Add(new SymbolToPayTXSizeFee
            {
                TokenSymbol = primarySymbol.Value,
                AddedTokenWeight = 1,
                BaseTokenWeight = 1
            });

            var symbolSetRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.SetSymbolsToPayTXSizeFee), newSymbolList);
            symbolSetRet.Status.ShouldBe(TransactionResultStatus.Failed);


            var newParliament = new Parliament.CreateOrganizationInput
            {
                ProposerAuthorityRequired = false,
                ProposalReleaseThreshold = new ProposalReleaseThreshold
                {
                    MaximalAbstentionThreshold = 1,
                    MaximalRejectionThreshold = 1,
                    MinimalApprovalThreshold = 1,
                    MinimalVoteThreshold = 1
                },
                ParliamentMemberProposingAllowed = false
            };
            var parliamentCreateRet = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateOrganization), newParliament);
            var newParliamentAddress = new Address();
            newParliamentAddress.MergeFrom(parliamentCreateRet.ReturnValue);
            var newAuthority = new AuthorityInfo
            {
                ContractAddress = ParliamentAddress,
                OwnerAddress = newParliamentAddress
            };
            var parliamentOrgRet = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.GetDefaultOrganizationAddress), new Empty());
            parliamentOrgRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var defaultParliamentAddress = new Address();
            defaultParliamentAddress.MergeFrom(parliamentOrgRet.ReturnValue);

            var createProposalInput = new CreateProposalInput
            {
                ToAddress = TokenContractAddress,
                Params = newAuthority.ToByteString(),
                OrganizationAddress = defaultParliamentAddress,
                ContractMethodName = nameof(TokenContractImplContainer.TokenContractImplStub
                    .ChangeSymbolsToPayTXSizeFeeController),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                createProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);

            var updateInput = new CreateProposalInput
            {
                ToAddress = TokenContractAddress,
                Params = newSymbolList.ToByteString(),
                OrganizationAddress = newParliamentAddress,
                ContractMethodName = nameof(TokenContractImplContainer.TokenContractImplStub.SetSymbolsToPayTXSizeFee),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var updateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal), updateInput);
            var updateProposalId = new Hash();
            updateProposalId.MergeFrom(updateProposal.ReturnValue);

            await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.Approve), updateProposalId);
            await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.Release), updateProposalId);

            symbolSetRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetSymbolsToPayTXSizeFee), newSymbolList);
            symbolSetRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var updatedSymbolList = new SymbolListToPayTXSizeFee();
            updatedSymbolList.MergeFrom(symbolSetRet.ReturnValue);
            updatedSymbolList.SymbolsToPayTxSizeFee.Count.ShouldBe(1);
        }

        [Fact]
        public async Task Update_Coefficient_For_Sender_Should_Success()
        {
            await CreateAndIssueVoteToken();
            const int pieceKey = 1000000;
            var updateInput = new CoefficientFromSender
            {
                LinerCoefficient = new LinerCoefficient
                {
                    ConstantValue = 1,
                    Denominator = 2,
                    Numerator = 3
                },
                PieceKey = pieceKey,
                IsLiner = true
            };
            var proposalId = await CreateToRootForUserFeeByTwoLayer(updateInput);
            await ApproveToRootForUserFeeByTwoLayer(proposalId);
            await VoteToReferendum(proposalId);
            await ReleaseToRootForUserFeeByTwoLayer(proposalId);

            var userCoefficientRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetCalculateFeeCoefficientOfSender), new Empty());
            userCoefficientRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var userCoefficient = new CalculateFeeCoefficientsOfType();
            userCoefficient.MergeFrom(userCoefficientRet.ReturnValue);
            var hasModified = userCoefficient.Coefficients.Single(x => x.PieceKey == pieceKey);
            hasModified.CoefficientDic["ConstantValue".ToLower()].ShouldBe(1);
            hasModified.CoefficientDic["Denominator".ToLower()].ShouldBe(2);
            hasModified.CoefficientDic["Numerator".ToLower()].ShouldBe(3);
        }

        [Fact]
        public async Task Update_Coefficient_For_Contract_Should_Success()
        {
            const int pieceKey = 1000000;
            const FeeTypeEnum feeType = FeeTypeEnum.Traffic;
            var updateInput = new CoefficientFromContract
            {
                FeeType = feeType,
                Coefficient = new CoefficientFromSender
                {
                    LinerCoefficient = new LinerCoefficient
                    {
                        ConstantValue = 1,
                        Denominator = 2,
                        Numerator = 3
                    },
                    PieceKey = pieceKey,
                    IsLiner = true
                }
            };

            var proposalId = await CreateToRootForDeveloperFeeByTwoLayer(updateInput);
            await ApproveToRootForDeveloperFeeByTwoLayer(proposalId);

            var middleApproveProposalId = await ApproveToRootForDeveloperFeeByMiddleLayer(proposalId);
            await ApproveThenReleaseMiddleProposalForDeveloper(middleApproveProposalId);

            await ReleaseToRootForDeveloperFeeByTwoLayer(proposalId);

            var developerCoefficientRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetCalculateFeeCoefficientOfContract), new SInt32Value
                {
                    Value = (int) feeType
                });
            developerCoefficientRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var userCoefficient = new CalculateFeeCoefficientsOfType();
            userCoefficient.MergeFrom(developerCoefficientRet.ReturnValue);
            var hasModified = userCoefficient.Coefficients.Single(x => x.PieceKey == pieceKey);
            hasModified.CoefficientDic["ConstantValue".ToLower()].ShouldBe(1);
            hasModified.CoefficientDic["Denominator".ToLower()].ShouldBe(2);
            hasModified.CoefficientDic["Numerator".ToLower()].ShouldBe(3);
        }

        [Fact]
        public async Task Update_Coefficient_PieceKey_Test()
        {
            const int pieceKey = int.MaxValue;
            const int newPieceKey = 999999;
            const FeeTypeEnum feeType = FeeTypeEnum.Read;
            var updateInput = new CoefficientFromContract
            {
                FeeType = feeType,
                Coefficient = new CoefficientFromSender
                {
                    PieceKey = pieceKey,
                    IsChangePieceKey = true,
                    NewPieceKeyCoefficient = new NewPieceKeyCoefficient
                    {
                        NewPieceKey = newPieceKey
                    }
                }
            };

            var proposalId = await CreateToRootForDeveloperFeeByTwoLayer(updateInput);
            await ApproveToRootForDeveloperFeeByTwoLayer(proposalId);

            var middleApproveProposalId = await ApproveToRootForDeveloperFeeByMiddleLayer(proposalId);
            await ApproveThenReleaseMiddleProposalForDeveloper(middleApproveProposalId);

            await ReleaseToRootForDeveloperFeeByTwoLayer(proposalId);

            var developerCoefficientRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetCalculateFeeCoefficientOfContract), new SInt32Value
                {
                    Value = (int) feeType
                });
            developerCoefficientRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var userCoefficient = new CalculateFeeCoefficientsOfType();
            userCoefficient.MergeFrom(developerCoefficientRet.ReturnValue);
            var hasModified = userCoefficient.Coefficients.Single(x => x.PieceKey == newPieceKey);
            hasModified.ShouldNotBeNull();
        }

        [Fact]
        public async Task Update_Coefficient_PowerAlgorithm_Test()
        {
            const int pieceKey = int.MaxValue;
            const FeeTypeEnum feeType = FeeTypeEnum.Read;
            var updateInput = new CoefficientFromContract
            {
                FeeType = feeType,
                Coefficient = new CoefficientFromSender
                {
                    PieceKey = pieceKey,
                    PowerCoefficient = new PowerCoefficient
                    {
                        Numerator = 2,
                        Denominator = 8,
                        ChangeSpanBase = 6,
                        ConstantValue = 100,
                        Weight = 300,
                        WeightBase = 50
                    },
                    IsLiner = false
                }
            };

            var proposalId = await CreateToRootForDeveloperFeeByTwoLayer(updateInput);
            await ApproveToRootForDeveloperFeeByTwoLayer(proposalId);

            var middleApproveProposalId = await ApproveToRootForDeveloperFeeByMiddleLayer(proposalId);
            await ApproveThenReleaseMiddleProposalForDeveloper(middleApproveProposalId);

            await ReleaseToRootForDeveloperFeeByTwoLayer(proposalId);

            var developerCoefficientRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetCalculateFeeCoefficientOfContract), new SInt32Value
                {
                    Value = (int) feeType
                });
            developerCoefficientRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var userCoefficient = new CalculateFeeCoefficientsOfType();
            userCoefficient.MergeFrom(developerCoefficientRet.ReturnValue);
            var hasModified = userCoefficient.Coefficients.Single(x => x.PieceKey == pieceKey);
            hasModified.CoefficientDic["Numerator".ToLower()].ShouldBe(2);
            hasModified.CoefficientDic["Denominator".ToLower()].ShouldBe(8);
            hasModified.CoefficientDic["ChangeSpanBase".ToLower()].ShouldBe(6);
            hasModified.CoefficientDic["ConstantValue".ToLower()].ShouldBe(100);
            hasModified.CoefficientDic["Weight".ToLower()].ShouldBe(300);
            hasModified.CoefficientDic["WeightBase".ToLower()].ShouldBe(50);
        }

        [Fact]
        public async Task MethodFeeController_Test()
        {
            var byteResult = await MainChainTester.CallContractMethodAsync(TokenContractAddress,
                nameof(MethodFeeProviderContractContainer.MethodFeeProviderContractStub.GetMethodFeeController),
                new Empty());
            var defaultController = AuthorityInfo.Parser.ParseFrom(byteResult);

            var createOrganizationResult =
                await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                    nameof(ParliamentContractContainer.ParliamentContractStub.CreateOrganization),
                    new CreateOrganizationInput
                    {
                        ProposalReleaseThreshold = new ProposalReleaseThreshold
                        {
                            MinimalApprovalThreshold = 1000,
                            MinimalVoteThreshold = 1000
                        }
                    });
            var organizationAddress = Address.Parser.ParseFrom(createOrganizationResult.ReturnValue);

            //change controller
            var newController = new AuthorityInfo
            {
                ContractAddress = defaultController.ContractAddress,
                OwnerAddress = organizationAddress
            };
            var createProposalInput = new CreateProposalInput
            {
                ToAddress = TokenContractAddress,
                Params = newController.ToByteString(),
                OrganizationAddress = defaultController.OwnerAddress,
                ContractMethodName = nameof(MethodFeeProviderContractContainer.MethodFeeProviderContractStub
                    .ChangeMethodFeeController),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                createProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var releaseRet = await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            releaseRet.Status.ShouldBe(TransactionResultStatus.Mined);

            byteResult = await MainChainTester.CallContractMethodAsync(TokenContractAddress,
                nameof(MethodFeeProviderContractContainer.MethodFeeProviderContractStub.GetMethodFeeController),
                new Empty());
            var queryController = AuthorityInfo.Parser.ParseFrom(byteResult);
            queryController.ShouldBe(newController);
        }

        [Fact]
        public async Task Change_CrossChainTokenContract_RegistrationController_Test()
        {
            var createOrganizationResult = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateOrganization),
                new Parliament.CreateOrganizationInput
                {
                    ProposalReleaseThreshold = new ProposalReleaseThreshold
                    {
                        MaximalAbstentionThreshold = 1,
                        MaximalRejectionThreshold = 1,
                        MinimalApprovalThreshold = 1,
                        MinimalVoteThreshold = 1
                    }
                });

            var newOrganization = Address.Parser.ParseFrom(createOrganizationResult.ReturnValue);

            var transactionResult = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub
                    .GetCrossChainTokenContractRegistrationController),
                new Empty());
            var defaultController = AuthorityInfo.Parser.ParseFrom(transactionResult.ReturnValue);
            var transactionResult2 = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.GetDefaultOrganizationAddress),
                new Empty());
            var defaultOrganization = Address.Parser.ParseFrom(transactionResult2.ReturnValue);
            defaultController.OwnerAddress.ShouldBe(defaultOrganization);

            const string proposalCreationMethodName = nameof(TokenContractImplContainer.TokenContractImplStub
                .ChangeCrossChainTokenContractRegistrationController);
            var newAuthority = new AuthorityInfo
            {
                ContractAddress = ParliamentAddress,
                OwnerAddress = newOrganization
            };
            var proposalId = await CreateProposalAsync(MainChainTester, ParliamentAddress,
                proposalCreationMethodName, newAuthority.ToByteString(),
                TokenContractAddress);

            await ApproveWithMinersAsync(proposalId, ParliamentAddress, MainChainTester);
            var txResult = await ReleaseProposalAsync(proposalId, ParliamentAddress, MainChainTester);
            txResult.Status.ShouldBe(TransactionResultStatus.Mined);

            var txResult2 = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub
                    .GetCrossChainTokenContractRegistrationController),
                new Empty());
            ;
            var newController = AuthorityInfo.Parser.ParseFrom(txResult2.ReturnValue);
            Assert.True(newController.OwnerAddress == newOrganization);
        }

        [Fact]
        public async Task Change_CrossChainTokenContract_RegistrationController_WithoutAuth_Test()
        {
            var createOrganizationResult = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateOrganization),
                new Parliament.CreateOrganizationInput
                {
                    ProposalReleaseThreshold = new ProposalReleaseThreshold
                    {
                        MaximalAbstentionThreshold = 1,
                        MaximalRejectionThreshold = 1,
                        MinimalApprovalThreshold = 1,
                        MinimalVoteThreshold = 1
                    }
                });

            var newOrganization = Address.Parser.ParseFrom(createOrganizationResult.ReturnValue);
            var newAuthority = new AuthorityInfo
            {
                ContractAddress = ParliamentAddress,
                OwnerAddress = newOrganization
            };
            var result = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub
                    .ChangeCrossChainTokenContractRegistrationController),
                newAuthority);
            result.Status.ShouldBe(TransactionResultStatus.Failed);
            result.Error.Contains("No permission.").ShouldBeTrue();
        }

        private async Task<Hash> CreateToRootForDeveloperFeeByTwoLayer(CoefficientFromContract input)
        {
            var organizations = await GetControllerForDeveloperFee();
            var createNestProposalInput = new CreateProposalInput
            {
                ToAddress = TokenContractAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.RootController.OwnerAddress,
                ContractMethodName = nameof(TokenContractImplContainer.TokenContractImplStub.UpdateCoefficientFromContract),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var createProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = createNestProposalInput.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.CreateProposal),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                createProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var releaseRet = await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var id = ProposalCreated.Parser
                .ParseFrom(releaseRet.Logs.First(l => l.Name.Contains(nameof(ProposalCreated)))
                    .NonIndexed).ProposalId;
            return id;
        }

        private async Task ApproveToRootForDeveloperFeeByTwoLayer(Hash input)
        {
            var organizations = await GetControllerForDeveloperFee();
            var approveProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                approveProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task ReleaseToRootForDeveloperFeeByTwoLayer(Hash input)
        {
            var organizations = await GetControllerForDeveloperFee();
            var releaseProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Release),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                releaseProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task<Hash> ApproveToRootForDeveloperFeeByMiddleLayer(Hash input)
        {
            var organizations = await GetControllerForDeveloperFee();
            var approveMidProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.DeveloperController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var approveLeafProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = approveMidProposalInput.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.CreateProposal),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                approveLeafProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var newCreateProposalRet =
                await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);

            var middleProposalId = ProposalCreated.Parser
                .ParseFrom(newCreateProposalRet.Logs.First(l => l.Name.Contains(nameof(ProposalCreated)))
                    .NonIndexed).ProposalId;
            return middleProposalId;
        }

        private async Task ApproveThenReleaseMiddleProposalForDeveloper(Hash input)
        {
            var organizations = await GetControllerForDeveloperFee();
            var approveLeafProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                approveLeafProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);

            approveLeafProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Release),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                approveLeafProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task<Hash> CreateToRootForUserFeeByTwoLayer(CoefficientFromSender input)
        {
            var organizations = await GetControllerForUserFee();
            var createNestProposalInput = new CreateProposalInput
            {
                ToAddress = TokenContractAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.RootController.OwnerAddress,
                ContractMethodName = nameof(TokenContractImplContainer.TokenContractImplStub.UpdateCoefficientFromSender),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };

            var createProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = createNestProposalInput.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.CreateProposal),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                createProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var releaseRet = await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);

            var id = ProposalCreated.Parser
                .ParseFrom(releaseRet.Logs.First(l => l.Name.Contains(nameof(ProposalCreated)))
                    .NonIndexed).ProposalId;
            return id;
        }

        private async Task ApproveToRootForUserFeeByTwoLayer(Hash input)
        {
            var organizations = await GetControllerForUserFee();
            var approveProposalInput = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                approveProposalInput);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task VoteToReferendum(Hash input)
        {
            var organizations = await GetControllerForUserFee();

            var referendumProposal = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ReferendumController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentProposal = new CreateProposalInput
            {
                ToAddress = ReferendumAddress,
                Params = referendumProposal.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(ReferendumContractContainer.ReferendumContractStub.CreateProposal),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                parliamentProposal);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var ret = await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            var id = ProposalCreated.Parser
                .ParseFrom(ret.Logs.First(l => l.Name.Contains(nameof(ProposalCreated)))
                    .NonIndexed).ProposalId;
            await MainChainTester.ExecuteContractWithMiningAsync(ReferendumAddress,
                nameof(ReferendumContractContainer.ReferendumContractStub.Approve),
                id);

            parliamentProposal = new CreateProposalInput
            {
                ToAddress = ReferendumAddress,
                Params = id.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(ReferendumContractContainer.ReferendumContractStub.Release),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                parliamentProposal);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task ReleaseToRootForUserFeeByTwoLayer(Hash input)
        {
            var organizations = await GetControllerForUserFee();
            var parliamentProposal = new CreateProposalInput
            {
                ToAddress = AssociationAddress,
                Params = input.ToByteString(),
                OrganizationAddress = organizations.ParliamentController.OwnerAddress,
                ContractMethodName = nameof(AssociationContractContainer.AssociationContractStub.Release),
                ExpiredTime = TimestampHelper.GetUtcNow().AddHours(1)
            };
            var parliamentCreateProposal = await MainChainTester.ExecuteContractWithMiningAsync(ParliamentAddress,
                nameof(ParliamentContractContainer.ParliamentContractStub.CreateProposal),
                parliamentProposal);
            parliamentCreateProposal.Status.ShouldBe(TransactionResultStatus.Mined);
            var parliamentProposalId = new Hash();
            parliamentProposalId.MergeFrom(parliamentCreateProposal.ReturnValue);
            await ApproveWithMinersAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
            await ReleaseProposalAsync(parliamentProposalId, ParliamentAddress, MainChainTester);
        }

        private async Task<UserFeeController> GetControllerForUserFee()
        {
            var organizationInfoRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetUserFeeController), new Empty());
            organizationInfoRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var organizationInfo = new UserFeeController();
            organizationInfo.MergeFrom(organizationInfoRet.ReturnValue);
            return organizationInfo;
        }

        private async Task<DeveloperFeeController> GetControllerForDeveloperFee()
        {
            var organizationInfoRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetDeveloperFeeController), new Empty());
            organizationInfoRet.Status.ShouldBe(TransactionResultStatus.Mined);
            var organizationInfo = new DeveloperFeeController();
            organizationInfo.MergeFrom(organizationInfoRet.ReturnValue);
            return organizationInfo;
        }

        private async Task CreateAndIssueVoteToken()
        {
            var callOwner = Address.FromPublicKey(MainChainTester.KeyPair.PublicKey);
            var primaryTokenRet = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.GetPrimaryTokenSymbol), new Empty());
            var symbol = new StringValue();
            symbol.MergeFrom(primaryTokenRet.ReturnValue);

            await MainChainTester.ExecuteContractWithMiningAsync(ReferendumAddress,
                nameof(ReferendumContractContainer.ReferendumContractStub.Initialize), new Empty());
            var issueResult = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.Issue), new IssueInput
                {
                    Amount = 100000,
                    To = callOwner,
                    Symbol = symbol.Value
                });
            issueResult.Status.ShouldBe(TransactionResultStatus.Mined);

            var approveResult = await MainChainTester.ExecuteContractWithMiningAsync(TokenContractAddress,
                nameof(TokenContractImplContainer.TokenContractImplStub.Approve), new ApproveInput
                {
                    Spender = ReferendumAddress,
                    Symbol = symbol.Value,
                    Amount = 100000
                });
            approveResult.Status.ShouldBe(TransactionResultStatus.Mined);
        }
    }
}