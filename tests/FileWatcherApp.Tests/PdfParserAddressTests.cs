using System;
using System.Collections.Generic;
using Xunit;
using FileWatcherApp; // Assuming PdfParser is in this namespace

namespace FileMonitor.Tests;

public class PdfParserAddressTests
{
    [Fact]
    public void Parse_UnstructuredPdf_ExtractsAllFields()
    {
        // Raw text content from /home/nr/Ordem de Produção nº 120435.pdf as extracted by PdfPig
        string allText = @"
WindSoft Sistemas FERREIRA IND COM FACAS CORTE VINCO LTDA - EPP Página: 1          
Data:  12/11/2025 10:55 Ordem de Produção Usuário: JOAO                            
Código Produto                                                                     
Nº O.P.                                                                            
Descrição do Produto                                                               
FACA CAIXA FACA O.S 48864_NF03098                                                  
120435 12/11/2025                                                                  
0000000000025                                                                      
Cód Cliente Nome/Razão Social do Cliente                                           
Telefone                                                                           
01276 YCAR ARTES GRÁFICAS LIMITADA                                                 
3531-6638-6607                                                                     
CEP Endereço (rua, nº, complemento, bairro) Cidade/UF                              
09691-350  RUA LIBERO BADARO 1201  - PAULICEIA SAO BERNARDO DO CAMPO/SP            
Email                                                                              
luiza@ycar.com.br;kelly@ycar.com.br;terceiros@ycar.com.br                          
Data                                                                               
CNPJ/CPF                                                                           
53.856.829/0001-57                                                                 
Inscrição                                                                          
635511994111                                                                       
Unid                                                                               
UN                                                                                 
Observação                                                                         
1                                                                                  
Quantidade                                                                         
Matéria prima utilizada na produção                                                
Código Descrição da matéria prima Unid Quantidade                                  
0000000000.33 COMP 18MM 11 LAM NORMAL M3 0,0042                                    
0000000000010 CORTE 2PT 23,80 SCANDIE CARTAO MT 10,1                               
0000002020416 MISTA 2PT 5,0 X 5,0 X 23,2 C 23,80 MT 0,4                            
0000000021322 PICOTE TRAVADO 2PT 2,0 X 1,0 X 2,0 C 23,60 MAIS MT 1,6               
0000000LVN001 VINCO 2PT 23,30 MT 6,7                                               
Data Etapa/Eventos Operador ObservaçõesInício Fim                                  
Data Emissão Data Entrega RG                                                       
WS-SisCom Sistema Gerencial                                                        
120435 12/11/2025 /     /                                                          
Assinatura ClienteNº Ordem de Produção                                             
";

        var parsedOp = PdfParser.Parse(allText);

        Assert.Equal("120435", parsedOp.NumeroOp);
        Assert.Equal("YCAR ARTES GRÁFICAS LIMITADA", parsedOp.Cliente);
        Assert.Equal("YCAR ARTES GRÁFICAS LIMITADA", parsedOp.ClienteNomeOficial);
        Assert.Equal("2025-11-12", parsedOp.DataOpIso);
        Assert.Equal("JOAO", parsedOp.Usuario);
        
        // Assert new fields
        Assert.Equal("53.856.829/0001-57", parsedOp.CnpjCpf);
        Assert.Equal("635511994111", parsedOp.InscricaoEstadual);
        Assert.Equal("3531-6638-6607", parsedOp.Telefone);
        Assert.Equal("luiza@ycar.com.br;kelly@ycar.com.br;terceiros@ycar.com.br", parsedOp.Email);

        // Assert EnderecoSugerido
        Assert.NotNull(parsedOp.EnderecosSugeridos);
        var endereco = Assert.Single(parsedOp.EnderecosSugeridos); // Assuming only one address is extracted
        Assert.NotNull(endereco);
        
        Assert.Equal("09691-350", endereco.Cep);
        Assert.Equal("RUA LIBERO BADARO 1201", endereco.Logradouro);
        Assert.Equal("PAULICEIA", endereco.Bairro);
        Assert.Equal("SAO BERNARDO DO CAMPO", endereco.Cidade);
        Assert.Equal("SP", endereco.Uf);
        Assert.Null(endereco.HorarioFuncionamento); // Not extracted by new logic
        Assert.Null(endereco.PadraoEntrega); // Not in the parsed text for this section
    }
}
