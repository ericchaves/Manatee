{
    up:{
        create_table:{
            name:'SecondTable',
            columns:[
                {name:'name', type:'string'},
                {name:'description', type:'text'},
                {name:'firstTableID',type:'int'}
             ],
            timestamps:true
         }
    }
}